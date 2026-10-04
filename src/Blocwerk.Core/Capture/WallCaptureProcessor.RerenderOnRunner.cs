// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// A texture re-render that goes to a 3D runner (the quality would not fit the host's memory budget in full): the capture's
/// mark says so (<see cref="CaptureTextureOutcome.RerenderOnRunner"/>), a <see cref="GpuJobKind.Textures"/> job is queued and the
/// mark then names it. Nothing waits here: the pass that queued it returns, and the GPU queue wakes the re-render worker when
/// the runner delivers (or the job ends), so the delivered zip is installed through the same
/// <c>StoreTexturesAsync</c> as a render on the host. The re-render's own budget (<see cref="MaxRedoStarts"/>) counts the
/// installs, never the waiting.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    private async Task<bool> RerenderOnRunnerAsync(Guid captureId, RerenderTarget target, CancellationToken ct)
    {
        if (gpuJobs is null || gpuJobs.Options.Mode == GpuRunnerMode.Off)
        {
            await EndRerenderAsync(captureId, target.ModelId, null, "3D runners are switched off on this server.", ct);
            return false;
        }

        var wanted = Guid.TryParse(target.JobId, out var parsed) ? parsed : (Guid?)null;
        var job = wanted is { } id ? await gpuJobs.TexturesJobAsync(id, ct) : null;
        if (job is null)
        {
            await QueueOnRunnerAsync(captureId, target, wanted ?? Guid.NewGuid(), ct);
            return false;
        }

        switch (job.Status)
        {
            case GpuJobStatus.Succeeded when job.InstalledAt is null:
                var installed = false;
                await RedoCountedAsync(captureId, CaptureRedoKind.Rerender, async () => installed = await InstallRunnerTexturesAsync(captureId, target, job, ct), ct);
                return installed;
            case GpuJobStatus.Succeeded:
                await EndRerenderAsync(captureId, target.ModelId, null, null, ct);
                return true;
            case GpuJobStatus.Failed or GpuJobStatus.Cancelled:
                var reason = $"The 3D model is active, but its textures could not be made on the 3D runner: {job.Error ?? "the job ended"}";
                await EndRerenderAsync(captureId, target.ModelId, null, reason, ct);
                await gpuJobs.CloseTexturesAsync(job.Id, job.Error ?? reason, ct);
                return false;
            default:
                return false;
        }
    }

    /// <summary>Packs the photos the model solved, queues the job and records its id on the capture's mark.</summary>
    private async Task QueueOnRunnerAsync(Guid captureId, RerenderTarget target, Guid jobId, CancellationToken ct)
    {
        try
        {
            await UpdateAsync(captureId, c => c.TexturesJobId = CaptureTextureOutcome.Mark(target.Quality, jobId.ToString(), onRunner: true), ct);
            var (geometry, photos) = await TexturesInputAsync(captureId, target.ModelId, ct);
            var (bundle, bytes, sha) = await RunnerTexturesBundle.StoreAsync(
                files, geometry, TextureQualityPresets.ToOptionsJson(target.Quality, settings.GeometryTextures), photos, gpuJobs!.Options.MaxBundleBytes, ct);
            await gpuJobs.EnqueueAsync(
                new GpuJob
                {
                    Id = jobId,
                    WallId = target.WallId,
                    CaptureId = captureId,
                    GeometryModelId = target.ModelId,
                    Kind = GpuJobKind.Textures,
                    Quality = SplatQuality.Draft,
                    RequiredMemoryMb = TextureQualityEstimate.RequiredMb(geometry, target.Quality, photos.Count, settings.GeometryTextures),
                    BundlePath = bundle,
                    BundleBytes = bytes,
                    BundleSha256 = sha,
                    PreparedPath = bundle,
                },
                ct);
            logger.LogInformation("Textures of capture {CaptureId} ({Quality}) queued for a 3D runner as GPU job {JobId}", captureId, target.Quality, jobId);
        }
        catch (Exception ex) when (ex is CaptureFailedException or InvalidDataException or IOException or DbUpdateException)
        {
            logger.LogWarning(ex, "Textures of capture {CaptureId} could not be queued for a 3D runner", captureId);
            var reason = ex is DbUpdateException ? "they could not be queued." : ex.Message;
            await EndRerenderAsync(captureId, target.ModelId, null, $"The 3D model is active, but its textures could not be made: {reason}", ct);
        }
    }

    /// <summary>The geometry the textures job renders (carried-over facets left out) and the photos the model solved.</summary>
    private async Task<(string Geometry, List<RunnerTexturesBundle.Photo> Photos)> TexturesInputAsync(Guid captureId, Guid modelId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var stored = await db.WallGeometryModels.Where(m => m.Id == modelId).Select(m => m.Json).FirstAsync(ct);
        var geometry = RegisteredGeometry.WithoutFacets(stored, RegisteredGeometry.Carried(stored).CarriedFacets);
        var cameras = CameraNames(geometry);
        var photos = (await LoadPhotosAsync(captureId, ct))
            .Select(p => new RunnerTexturesBundle.Photo(CaptureComputeDocuments.PhotoName(p.Index), p.StoredPath))
            .Where(p => cameras.Contains(p.Name))
            .ToList();
        return (geometry, photos);
    }

    /// <summary>Installs the delivered zip through the normal texture path and ends the re-render; true when the textures are made.</summary>
    private async Task<bool> InstallRunnerTexturesAsync(Guid captureId, RerenderTarget target, GpuJob job, CancellationToken ct)
    {
        try
        {
            var path = job.ResultPath is { } stored && files.ResolvePhysicalPath(stored) is { } p && File.Exists(p)
                ? p
                : throw new CaptureFailedException("the 3D runner's textures are missing on the server.");
            var status = RunnerTexturesResult.ToStatus(path, job.Id.ToString());
            await StoreTexturesAsync(captureId, target.ModelId, status, new RunnerTexturesClient(path), ct, silent: true);
        }
        catch (Exception ex) when (ex is CaptureFailedException or ComputeJobException or InvalidDataException or IOException or DbUpdateException)
        {
            logger.LogWarning(ex, "Installing the 3D runner's textures of capture {CaptureId} failed", captureId);
            var reason = ex is DbUpdateException ? "they could not be saved." : ex.Message;
            await EndRerenderAsync(captureId, target.ModelId, null, $"The 3D model is active, but its textures could not be made: {reason}", ct);
            await gpuJobs!.CloseTexturesAsync(job.Id, reason, ct);
            return false;
        }

        await EndRerenderAsync(captureId, target.ModelId, null, null, ct);
        await gpuJobs!.CloseTexturesAsync(job.Id, null, ct);
        logger.LogInformation("Textures of capture {CaptureId} rendered again on a 3D runner (job {JobId})", captureId, job.Id);
        return true;
    }
}
