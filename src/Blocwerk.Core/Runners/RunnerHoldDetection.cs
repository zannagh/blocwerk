// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The detection half of the post-capture hold proposal search on a 3D runner (<see cref="GpuJobKind.DetectHolds"/>): minutes
/// of CPU in the app (34 on the server for 425 photos) instead go to a runner that advertises <c>detect-holds</c>. Never
/// required: with the switch off, no such runner online and taking work, settings the runner cannot replay, no model file,
/// a job nobody claims within <see cref="GpuRunnerOptions.HoldsClaimWait"/>, one that fails, or a wait past
/// <see cref="GpuRunnerOptions.HoldsMaxWait"/>, the answer is null and the search detects in the app as it always did. A
/// restart while it waits finds the job again (<see cref="GpuJobQueue.OpenHoldsJobAsync"/>) instead of queueing another.
/// </summary>
public sealed class RunnerHoldDetection(
    GpuJobQueue gpuJobs,
    ICaptureFileStore files,
    RootDbContextFactory dbContextFactory,
    BlocwerkSettings settings,
    ILogger<RunnerHoldDetection> logger,
    TimeProvider? clock = null)
{
    private static readonly Dictionary<(string Path, long Length, DateTime Written), string> ModelHashes = [];
    private static readonly GpuJobStatus[] Waiting = [GpuJobStatus.Queued];
    private static readonly GpuJobStatus[] Active = [GpuJobStatus.Queued, GpuJobStatus.Claimed, GpuJobStatus.Running];
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    private GpuRunnerOptions Options => gpuJobs.Options;

    /// <summary>The holds a runner found per camera name, or null: then the app detects them itself.</summary>
    /// <param name="context">The capture and its (active) model.</param>
    /// <param name="ct">Cancellation (a shutdown: the job stays, a restart waits for it again).</param>
    /// <returns>The holds per photo, or null.</returns>
    public async Task<RunnerHoldsFound?> DetectAsync(CaptureFollowUpContext context, CancellationToken ct)
    {
        if (!Options.HoldsOnRunners || Options.Mode == GpuRunnerMode.Off)
        {
            return null;
        }

        var job = await gpuJobs.OpenHoldsJobAsync(context.CaptureId, context.ModelId, ct);
        if (job is null)
        {
            if (!await gpuJobs.HoldsRunnerReadyAsync(context.WallId, ct))
            {
                return null;
            }

            job = await QueueAsync(context, ct);
        }

        return job is null ? null : await WaitAsync(job, ct);
    }

    /// <summary>
    /// The runner detector's identity, <c>yolo-runner:&lt;first 12 hex of the model's SHA-256&gt;</c>: what its detections are
    /// cached under, so they are never taken for the app detector's (or another model's).
    /// </summary>
    internal string DetectorName()
    {
        if (ModelFile() is not { } path)
        {
            return "yolo-runner:unknown";
        }

        var info = new FileInfo(path);
        var key = (path, info.Length, info.LastWriteTimeUtc);
        lock (ModelHashes)
        {
            if (ModelHashes.TryGetValue(key, out var known))
            {
                return known;
            }
        }

        using var stream = File.OpenRead(path);
        var name = $"yolo-runner:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))[..12].ToLowerInvariant()}";
        lock (ModelHashes)
        {
            ModelHashes[key] = name;
        }

        return name;
    }

    /// <summary>The model file the app's detector loads, resolved like the detector does; null when there is none.</summary>
    internal string? ModelFile()
    {
        var configured = settings.HoldDetection.ModelPath;
        var path = Path.IsPathRooted(configured) || !File.Exists(Path.Combine(AppContext.BaseDirectory, configured))
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
        return File.Exists(path) ? path : null;
    }

    private async Task<GpuJob?> QueueAsync(CaptureFollowUpContext context, CancellationToken ct)
    {
        var tiling = settings.HoldDetection.Tiling;
        if (ModelFile() is not { } model || !RunnerHoldsBundle.Replayable(tiling))
        {
            logger.LogInformation("Capture {CaptureId}: holds are detected in the app (no model file, or settings a runner cannot replay)", context.CaptureId);
            return null;
        }

        try
        {
            var photos = await PhotosAsync(context.ModelId, ct);
            var (bundle, bytes, sha, count) = await RunnerHoldsBundle.StoreAsync(files, model, tiling, photos, Options.MaxBundleBytes, ct);
            var job = await gpuJobs.EnqueueAsync(
                new GpuJob
                {
                    WallId = context.WallId, CaptureId = context.CaptureId, GeometryModelId = context.ModelId, Kind = GpuJobKind.DetectHolds,
                    Quality = SplatQuality.Draft, BundlePath = bundle, BundleBytes = bytes, BundleSha256 = sha, PreparedPath = bundle,
                },
                ct);
            logger.LogInformation("Capture {CaptureId}: hold detection of {Photos} photos queued for a 3D runner as GPU job {JobId}", context.CaptureId, count, job.Id);
            return job;
        }
        catch (Exception ex) when (ex is CaptureFailedException or IOException or InvalidDataException or DbUpdateException)
        {
            logger.LogWarning(ex, "Capture {CaptureId}: hold detection could not be queued for a 3D runner; detecting in the app", context.CaptureId);
            return null;
        }
    }

    /// <summary>The photos the search reads: every camera of the model that has a stored capture photo.</summary>
    private async Task<List<(string Name, string StoredPath)>> PhotosAsync(Guid modelId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var json = await db.WallGeometryModels.AsNoTracking().Where(m => m.Id == modelId).Select(m => m.Json).FirstAsync(ct);
        var photos = await HoldProposalService.CapturePhotosAsync(db, modelId, ct);
        return SolvedCamera.ParseAll(json).Where(c => photos.ContainsKey(c.Image)).Select(c => (c.Image, photos[c.Image])).ToList();
    }

    private async Task<RunnerHoldsFound?> WaitAsync(GpuJob queued, CancellationToken ct)
    {
        var id = queued.Id;
        DateTimeOffset? waitingSince = null;
        while (true)
        {
            var job = await gpuJobs.HoldsJobAsync(id, ct);
            var now = time.GetUtcNow();
            switch (job?.Status)
            {
                case null:
                    return null;
                case GpuJobStatus.Succeeded:
                    return await TakeResultAsync(job, ct);
                case GpuJobStatus.Failed or GpuJobStatus.Cancelled:
                    logger.LogInformation("GPU job {JobId} (hold detection) ended: {Reason}; detecting in the app", id, job.Error);
                    await gpuJobs.CloseHoldsAsync(id, job.Error ?? "the job ended", ct);
                    return null;
                case GpuJobStatus.Queued:
                    waitingSince ??= job.ClaimedAt is null ? job.CreatedAt : now;
                    break;
                default:
                    waitingSince = null;
                    break;
            }

            if (await GiveUpAsync(job, waitingSince, now, ct))
            {
                return null;
            }

            await Task.Delay(Options.HoldsPollInterval, ct);
        }
    }

    /// <summary>
    /// Cancels the job (and the wait) once nobody claimed it within the claim wait, or the whole wait passed its cap. False
    /// when it goes on waiting, also when the cancel lost to a runner that claimed or delivered meanwhile.
    /// </summary>
    private async Task<bool> GiveUpAsync(GpuJob job, DateTimeOffset? waitingSince, DateTimeOffset now, CancellationToken ct)
    {
        if (now - job.CreatedAt > Options.HoldsMaxWait)
        {
            var reason = $"no 3D runner finished finding the holds within {Options.HoldsMaxWait.TotalMinutes:0} minutes; the server does it";
            return await gpuJobs.CancelHoldsAsync(job.Id, reason, Active, ct);
        }

        if (waitingSince is { } since && now - since > Options.HoldsClaimWait)
        {
            var reason = $"no 3D runner took the hold search within {Options.HoldsClaimWait.TotalMinutes:0} minutes; the server does it";
            return await gpuJobs.CancelHoldsAsync(job.Id, reason, Waiting, ct);
        }

        return false;
    }

    private async Task<RunnerHoldsFound?> TakeResultAsync(GpuJob job, CancellationToken ct)
    {
        try
        {
            var path = job.ResultPath is { } stored && files.ResolvePhysicalPath(stored) is { } p && File.Exists(p)
                ? p
                : throw new InvalidDataException("the runner's result is missing on the server");
            var holds = RunnerHoldsResult.Read(path);
            await gpuJobs.CloseHoldsAsync(job.Id, null, ct);
            logger.LogInformation("GPU job {JobId}: a 3D runner found the holds in {Photos} photos", job.Id, holds.Count);
            return new RunnerHoldsFound(DetectorName(), holds);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            logger.LogWarning(ex, "GPU job {JobId}: the holds the runner found could not be read; detecting in the app", job.Id);
            await gpuJobs.CloseHoldsAsync(job.Id, ex.Message, ct);
            return null;
        }
    }
}
