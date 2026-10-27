// <copyright file="HoldTexturePlacementService.SurfaceJob.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// "Place holds on the 3D surface", step 1: the wall's live panel photos are registered into the active model's capture
/// cameras on a 3D runner (<see cref="GpuJobKind.RegisterPanels"/>). The poses it delivers stay with the job; step 2
/// (<c>HoldTexturePlacementService.Surface</c>) reads them.
/// </summary>
public sealed partial class HoldTexturePlacementService
{
    /// <summary>A registration whose matched points fit worse than this (RMS, px) is not used.</summary>
    public const double MaxPoseRmsPx = 8;

    /// <summary>A registration with fewer matched points than this is not used.</summary>
    public const int MinPoseObservations = 200;

    /// <inheritdoc/>
    public async Task<SurfaceRegistration> GetSurfaceRegistrationAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            if (gpuJobs is null || await ActiveModelTextures.FindAsync(db, wallId, ct) is not { } model)
            {
                return SurfaceRegistration.None;
            }

            var job = await gpuJobs.LatestPanelsJobAsync(wallId, model.Id, ct);
            return SurfaceRegistration.Of(job, (await CurrentPosesAsync(db, job, ct)).Count)
                with { RunnerReady = await gpuJobs.RunnerReadyAsync(wallId, GpuJobKind.RegisterPanels, ct) };
        }
    }

    /// <inheritdoc/>
    public async Task<SurfaceRegistration> RegisterPanelsAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            if (gpuJobs is null || files is null)
            {
                throw new UserFacingException("3D runners are not set up on this server.");
            }

            var model = await ActiveModelTextures.FindAsync(db, wallId, ct)
                ?? throw new UserFacingException("This wall has no active 3D model to register the panel photos in.");

            // The per-wall lock makes "is one open? else queue one" atomic (one app instance).
            return await ExclusiveAsync(wallId, () => QueueUnlessOpenAsync(db, wallId, model.Id, model.Json, ct))
                   ?? throw new UserFacingException("Holds are already being placed on this wall. Try again in a minute.");
        }
    }

    private async Task<SurfaceRegistration> QueueUnlessOpenAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, string modelJson, CancellationToken ct)
    {
        if (await gpuJobs!.LatestPanelsJobAsync(wallId, modelId, ct) is { Status: GpuJobStatus.Queued or GpuJobStatus.Claimed or GpuJobStatus.Running } open)
        {
            return SurfaceRegistration.Of(open, 0);
        }

        if (!await gpuJobs.RunnerReadyAsync(wallId, GpuJobKind.RegisterPanels, ct))
        {
            throw new UserFacingException("No 3D runner that can register panel photos (it needs COLMAP) is online for this wall.");
        }

        var job = await QueuePanelsJobAsync(db, wallId, modelId, modelJson, ct);
        logger.LogInformation("Wall {WallId}: panel photos queued for registration on a 3D runner as GPU job {JobId}", wallId, job.Id);
        return SurfaceRegistration.Of(job, 0) with { RunnerReady = true };
    }

    /// <summary>
    /// The job's poses that fit well enough and belong to a live panel's CURRENT photo (a crop since moves the revision).
    /// </summary>
    private async Task<Dictionary<Guid, SolvedCamera>> CurrentPosesAsync(BlocwerkDbContext db, GpuJob? job, CancellationToken ct)
    {
        var poses = job is null ? null : gpuJobs!.PanelPoses(job);
        if (poses is null)
        {
            return [];
        }

        var ids = poses.Keys.ToList();
        var revisions = await db.WallPanels.AsNoTracking().Where(p => ids.Contains(p.Id) && p.Photo != null)
            .ToDictionaryAsync(p => p.Id, p => p.PhotoRevision, ct);
        return poses.Where(p => p.Value.Observations >= MinPoseObservations && p.Value.RmsPx <= MaxPoseRmsPx
                                && revisions.TryGetValue(p.Key, out var rev) && rev == p.Value.PhotoRevision)
            .ToDictionary(p => p.Key, p => p.Value.Camera);
    }

    private async Task<GpuJob> QueuePanelsJobAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, string modelJson, CancellationToken ct)
    {
        var captureId = await db.WallCaptures.AsNoTracking().Where(c => c.GeometryModelId == modelId)
            .OrderByDescending(c => c.CreatedAt).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct)
            ?? throw new UserFacingException("The active 3D model was not made from a capture, so there are no photos to register in.");
        var stored = await HoldProposalService.CapturePhotosAsync(db, modelId, ct);
        var photos = SolvedCamera.ParseAll(modelJson).Where(c => stored.ContainsKey(c.Image)).Select(c => (c, stored[c.Image])).ToList();
        var panelIds = await LiveWallHolds.LoadPanelIdsAsync(db, wallId, ct);
        var panels = await db.WallPanels.AsNoTracking().Where(p => panelIds.Contains(p.Id) && p.Photo != null)
            .Select(p => new { p.Id, p.PhotoRevision }).ToListAsync(ct);

        // One panel photo in memory at a time.
        async Task<byte[]?> PhotoAsync(Guid id, CancellationToken token) =>
            await db.WallPanels.AsNoTracking().Where(p => p.Id == id).Select(p => p.Photo).FirstOrDefaultAsync(token);
        try
        {
            var (bundle, bytes, sha) = await RunnerPanelsBundle.StoreAsync(
                files!, photos, panels.Select(p => (p.Id, p.PhotoRevision)).ToList(), PhotoAsync, gpuJobs!.Options.MaxBundleBytes, ct);
            return await gpuJobs.EnqueueAsync(
                new GpuJob
                {
                    WallId = wallId, CaptureId = captureId, GeometryModelId = modelId, Kind = GpuJobKind.RegisterPanels,
                    Quality = SplatQuality.Draft, BundlePath = bundle, BundleBytes = bytes, BundleSha256 = sha, PreparedPath = bundle,
                },
                ct);
        }
        catch (CaptureFailedException ex)
        {
            throw new UserFacingException($"The panel photos could not be sent to a 3D runner: {ex.Message}");
        }
    }
}
