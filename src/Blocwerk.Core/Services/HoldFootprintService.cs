// <copyright file="HoldFootprintService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Runs <see cref="HoldFootprintRefiner"/> on a wall: its live holds, its active model, the model's solved
/// capture cameras with the capture photos they belong to, and the outline segmenter. Writes only
/// <see cref="Hold.FootprintMm"/>, and only on holds whose outline did not change while it ran.
/// </summary>
public sealed class HoldFootprintService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    ILogger<HoldFootprintService> logger,
    IHoldOutlineService? outlineService = null,
    ICaptureFileStore? files = null,
    IKioskContext? kioskContext = null) : IHoldFootprintService
{
    private const string KioskRefusal = "Refining 3D hold shapes";

    /// <inheritdoc />
    public async Task<HoldFootprintRunResult> RefineAsync(Guid wallId, CancellationToken ct = default)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, KioskRefusal);
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, ct);
        }

        return await RunAsync(wallId, ct)
            ?? throw new InvalidOperationException("This wall has no active geometry model, or outline detection is off.");
    }

    /// <inheritdoc />
    public async Task<HoldFootprintRunResult?> RefineFromPipelineAsync(Guid wallId, CancellationToken ct = default)
    {
        try
        {
            return await RunAsync(wallId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Footprint refinement on wall {WallId} failed", wallId);
            return null;
        }
    }

    private async Task<HoldFootprintRunResult?> RunAsync(Guid wallId, CancellationToken ct)
    {
        if (outlineService is null)
        {
            return null;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var model = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive)
            .Select(m => new { m.Id, m.Json })
            .FirstOrDefaultAsync(ct);
        var generation = await db.Walls.Where(w => w.Id == wallId).Select(w => (int?)w.CurrentGeneration).FirstOrDefaultAsync(ct);
        if (model is null || generation is null)
        {
            return null;
        }

        var doc = WallGeometryDocument.Parse(model.Json);
        var cameras = SolvedCamera.ParseAll(model.Json);
        var photos = await CapturePhotosAsync(db, model.Id, ct);
        var live = await db.Holds.AsNoTracking().Where(h => h.WallId == wallId && h.Generation <= generation).ToListAsync(ct);
        var markers = await Wall3DPhotoMarkerLoader.LoadAsync(db, wallId, ct);
        var projector = HoldPlaneProjector.Create(live, doc, markers);
        var usable = cameras.Where(c => photos.ContainsKey(c.Image)).ToList();
        var refinement = await Task.Run(() => HoldFootprintRefiner.Refine(live, doc, usable, c => Open(c, photos, ct), projector), ct);
        var written = await WriteAsync(db, wallId, refinement, ct);
        logger.LogInformation(
            "Footprints on wall {WallId}: {Multi} multi-view, {Single} single-view, {Skipped} skipped, {Written} written, {Photos} capture photos",
            wallId, refinement.MultiView, refinement.SingleView, refinement.Skipped, written, usable.Count);
        return new HoldFootprintRunResult(refinement.MultiView, refinement.SingleView, refinement.Skipped, usable.Count);
    }

    /// <summary>The stored path of each photo of the capture that produced the model, by base file name.</summary>
    private async Task<Dictionary<string, string>> CapturePhotosAsync(BlocwerkDbContext db, Guid modelId, CancellationToken ct)
    {
        if (files is null)
        {
            return [];
        }

        var rows = await db.WallCapturePhotos.AsNoTracking()
            .Where(p => p.Capture.GeometryModelId == modelId && p.OriginalFileName != null)
            .Select(p => new { p.OriginalFileName, p.StoredPath })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => Path.GetFileNameWithoutExtension(r.OriginalFileName!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().StoredPath, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One capture photo's outline session, or null when unreadable or rotated against its camera.</summary>
    private IHoldOutlineSession? Open(SolvedCamera camera, Dictionary<string, string> photos, CancellationToken ct)
    {
        var bytes = files!.ReadAsync(photos[camera.Image], ct).GetAwaiter().GetResult();
        if (bytes is null)
        {
            return null;
        }

        try
        {
            var session = outlineService!.OpenSession(bytes);
            var sameOrientation = camera.Width <= 0 || (camera.Width >= camera.Height) == (session.ImageWidth >= session.ImageHeight);
            if (sameOrientation)
            {
                return session;
            }

            session.Dispose();
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Capture photo {Image} could not be decoded for footprints", camera.Image);
        }

        return null;
    }

    /// <summary>Stores the footprints on holds whose outline still matches; clears stale ones. Returns how many were written.</summary>
    private static async Task<int> WriteAsync(BlocwerkDbContext db, Guid wallId, HoldFootprintRefinement refinement, CancellationToken ct)
    {
        var ids = refinement.Footprints.Keys.ToList();
        var holds = await db.Holds.Where(h => h.WallId == wallId && ids.Contains(h.Id)).ToListAsync(ct);
        var written = 0;
        foreach (var hold in holds)
        {
            var fp = refinement.Footprints[hold.Id];
            if (fp.OutlineKey != HoldFootprint.KeyOf(hold))
            {
                continue;
            }

            var json = fp.ToJson();
            if (hold.FootprintMm != json)
            {
                hold.FootprintMm = json;
                written++;
            }
        }

        await db.SaveChangesAsync(ct);
        return written;
    }
}
