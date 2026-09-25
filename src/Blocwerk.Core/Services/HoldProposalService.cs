// <copyright file="HoldProposalService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Diagnostics;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Proposals;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IHoldProposalService"/>. A run reads the active model's capture photos one at a time (one decoded
/// photo in memory), detects holds with the first available <see cref="ICaptureHoldDetector"/>, and hands the
/// detections to <see cref="HoldProposalFinder"/> with the model's facets, its visible volumes and every live
/// placed hold as known. Writes only <see cref="HoldProposal"/> rows (pending ones are replaced).
/// </summary>
public sealed partial class HoldProposalService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    IWallService wallService,
    IEnumerable<ICaptureHoldDetector> detectors,
    ILogger<HoldProposalService> logger,
    ICaptureFileStore? files = null,
    IKioskContext? kioskContext = null) : IHoldProposalService
{
    private const string KioskRefusal = "Finding holds from all photos";

    /// <summary>One run per wall at a time (a run takes minutes).</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <inheritdoc />
    public async Task<HoldProposalRunResult> FindAsync(Guid wallId, CancellationToken ct = default)
    {
        await EnsureAdminAsync(wallId, ct);
        if (!await Gate.WaitAsync(0, ct))
        {
            throw new UserFacingException("A hold search is already running. Try again in a few minutes.");
        }

        try
        {
            return await RunAsync(wallId, ct)
                ?? throw new UserFacingException("This wall has no active 3D model with capture photos to search.");
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<HoldProposalRunResult?> FindFromPipelineAsync(Guid wallId, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await RunAsync(wallId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Hold proposals on wall {WallId} failed", wallId);
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task EnsureAdminAsync(Guid wallId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;
        KioskGuard.EnsureNotKiosk(kioskContext, db, KioskRefusal);
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, ct);
    }

    private async Task<HoldProposalRunResult?> RunAsync(Guid wallId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var model = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive).Select(m => new { m.Id, m.Json }).FirstOrDefaultAsync(ct);
        var detector = await FirstAvailableAsync(ct);
        if (model is null || files is null || detector is null)
        {
            return null;
        }

        var photos = await CapturePhotosAsync(db, model.Id, ct);
        var cameras = SolvedCamera.ParseAll(model.Json).Where(c => photos.ContainsKey(c.Image))
            .ToDictionary(c => c.Image, StringComparer.OrdinalIgnoreCase);
        if (cameras.Count < 2)
        {
            return null;
        }

        var watch = Stopwatch.StartNew();
        var detections = await DetectAllAsync(detector, cameras, photos, ct);
        var inputs = await ProposalInputs.LoadAsync(db, wallId, model.Id, WallGeometryDocument.Parse(model.Json), ct);
        var reviewed = await db.HoldProposals.AsNoTracking()
            .Where(p => p.WallId == wallId && p.Status != HoldProposalStatus.Pending)
            .Select(p => new[] { p.X, p.Y, p.Z }).ToListAsync(ct);
        var (candidates, clusters) = await Task.Run(() => HoldProposalFinder.Find(cameras, detections, inputs.Facets, inputs.Known, reviewed), ct);
        var onPanels = await ReplacePendingAsync(db, wallId, model.Id, candidates, inputs, ct);
        logger.LogInformation(
            "Hold proposals on wall {WallId}: {Photos} photos ({Detector}), {Detections} detections, {Clusters} multi-view holds, {Proposals} proposed ({OnPanels} on a panel photo), {Ms} ms",
            wallId, cameras.Count, detector.Name, detections.Count, clusters, candidates.Count, onPanels, watch.ElapsedMilliseconds);
        return new HoldProposalRunResult(cameras.Count, detections.Count, clusters, candidates.Count, onPanels, detector.Name);
    }

    private async Task<ICaptureHoldDetector?> FirstAvailableAsync(CancellationToken ct)
    {
        foreach (var d in detectors)
        {
            if (await d.IsAvailableAsync(ct))
            {
                return d;
            }
        }

        return null;
    }

    /// <summary>Detections of every photo whose decoded size matches its solved camera (rotated photos are skipped).</summary>
    private async Task<List<CaptureDetection>> DetectAllAsync(
        ICaptureHoldDetector detector, Dictionary<string, SolvedCamera> cameras, Dictionary<string, string> photos, CancellationToken ct)
    {
        var all = new List<CaptureDetection>();
        foreach (var (name, camera) in cameras)
        {
            ct.ThrowIfCancellationRequested();
            if (CaptureDetectionCache.Get(photos[name], detector.Name) is { } cached)
            {
                all.AddRange(cached);
                continue;
            }

            if (await files!.ReadAsync(photos[name], ct) is not { } bytes || PanelPhotoInfo.FromImage(bytes) is not { } info
                || (info.Width >= info.Height) != (camera.Width >= camera.Height))
            {
                continue;
            }

            // Cameras solved at another resolution: the detections are scaled onto the solved grid.
            var sx = camera.Width > 0 ? (double)camera.Width / info.Width : 1;
            var found = (await detector.DetectAsync(name, bytes, ct))
                .Select(d => d with { Px = d.Px * sx, Py = d.Py * sx, RadiusPx = d.RadiusPx * sx }).ToList();
            CaptureDetectionCache.Put(photos[name], detector.Name, found);
            all.AddRange(found);
        }

        return all;
    }

    private static async Task<Dictionary<string, string>> CapturePhotosAsync(BlocwerkDbContext db, Guid modelId, CancellationToken ct)
    {
        var rows = await db.WallCapturePhotos.AsNoTracking()
            .Where(p => p.Capture.GeometryModelId == modelId)
            .Select(p => new { p.Index, p.OriginalFileName, p.StoredPath })
            .ToListAsync(ct);
        return CameraPhotoNames.Map(rows.Select(r => (r.Index, r.OriginalFileName, r.StoredPath)));
    }
}
