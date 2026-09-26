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
using Blocwerk.Core.Geometry.Volumes;
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

        return await RunAsync(wallId, null, ct)
            ?? throw new UserFacingException("This wall has no active geometry model, or outline detection is off.");
    }

    /// <inheritdoc />
    public Task<HoldFootprintRunResult?> RefineFromPipelineAsync(Guid wallId, CancellationToken ct = default) =>
        RunSafelyAsync(wallId, null, ct);

    /// <inheritdoc />
    public Task<HoldFootprintRunResult?> RefineHoldsFromPipelineAsync(Guid wallId, IReadOnlyCollection<Guid> holdIds, CancellationToken ct = default) =>
        RunSafelyAsync(wallId, holdIds.ToHashSet(), ct);

    private async Task<HoldFootprintRunResult?> RunSafelyAsync(Guid wallId, IReadOnlySet<Guid>? only, CancellationToken ct)
    {
        try
        {
            return await RunAsync(wallId, only, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Footprint refinement on wall {WallId} failed", wallId);
            return null;
        }
    }

    private async Task<HoldFootprintRunResult?> RunAsync(Guid wallId, IReadOnlySet<Guid>? only, CancellationToken ct)
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
        if (model is null || !await db.Walls.AnyAsync(w => w.Id == wallId, ct))
        {
            return null;
        }

        var doc = WallGeometryDocument.Parse(model.Json);
        var cameras = SolvedCamera.ParseAll(model.Json);
        var photos = await CapturePhotosAsync(db, model.Id, ct);
        var live = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).AsNoTracking().ToListAsync(ct);
        var markers = await Wall3DPhotoMarkerLoader.LoadAsync(db, wallId, ct);
        var projector = HoldPlaneProjector.Create(live, doc, markers);
        var usable = cameras.Where(c => photos.ContainsKey(c.Image)).ToList();
        var panelPhotos = await PanelPhotoInfoLoader.LoadAsync(
            db, wallId, live.Where(h => h.FacetId is not null).Select(HoldPlaneProjector.PhotoOf), ct);
        var volumes = await FacetVolumesAsync(db, model.Id, doc, ct);
        var refinement = await Task.Run(
            () => HoldFootprintRefiner.Refine(live, doc, usable, c => Open(c, photos, ct), projector, only, panelPhotos, volumes), ct);
        LogPanelCameras(wallId, refinement);
        var written = await WriteAsync(db, wallId, refinement, Wall3DFallbackPlacement.FacetExtents(doc), ct);
        logger.LogInformation(
            "Footprints on wall {WallId}: {Multi} multi-view, {Single} single-view, {Skipped} skipped, {Written} written, {Photos} capture photos",
            wallId, refinement.MultiView, refinement.SingleView, refinement.Skipped, written, usable.Count);
        return new HoldFootprintRunResult(refinement.MultiView, refinement.SingleView, refinement.Skipped, usable.Count);
    }

    /// <summary>
    /// The stored path of each photo of the capture that produced the model, by the name its solved camera carries:
    /// <see cref="CaptureComputeDocuments.PhotoName"/> (<c>p01</c>…), the name the photos are sent to the geometry
    /// worker under. Models solved before that naming carry the base file name, which still resolves.
    /// </summary>
    private async Task<Dictionary<string, string>> CapturePhotosAsync(BlocwerkDbContext db, Guid modelId, CancellationToken ct)
    {
        if (files is null)
        {
            return [];
        }

        var rows = await db.WallCapturePhotos.AsNoTracking()
            .Where(p => p.Capture.GeometryModelId == modelId)
            .Select(p => new { p.Index, p.OriginalFileName, p.StoredPath })
            .ToListAsync(ct);
        return CameraPhotoNames.Map(rows.Select(r => (r.Index, r.OriginalFileName, r.StoredPath)));
    }

    /// <summary>The model's visible volumes per facet (<see cref="VolumeFootprints"/>); empty without any.</summary>
    private static async Task<Dictionary<string, FacetVolumes>> FacetVolumesAsync(
        BlocwerkDbContext db, Guid modelId, WallGeometryDocument doc, CancellationToken ct)
    {
        var rows = await db.WallVolumes.AsNoTracking().Where(v => v.GeometryModelId == modelId && !v.IsHidden).ToListAsync(ct);
        var result = new Dictionary<string, FacetVolumes>(StringComparer.Ordinal);
        foreach (var g in rows.GroupBy(v => v.FacetId))
        {
            var surfaces = g.Select(v => VolumeSurface.FromJson(v.SurfaceJson)).OfType<VolumeSurface>().ToList();
            if (doc.FindFacet(g.Key) is { } found && FacetFrame.From(found.Facet) is { } frame && surfaces.Count > 0)
            {
                result[g.Key] = new FacetVolumes(frame, surfaces);
            }
        }

        return result;
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

    private void LogPanelCameras(Guid wallId, HoldFootprintRefinement refinement)
    {
        foreach (var (key, e) in refinement.PanelCameras ?? new Dictionary<Wall3DPhotoKey, PanelCameraEstimate>())
        {
            if (e.Centre is { } c)
            {
                logger.LogInformation(
                    "Footprints on wall {WallId}: photo {PanelId}/{Generation} camera by {Method} from {Used}/{Points} holds: "
                    + "{Distance:F0} mm in front of the wall, height {Height:F0} mm, reprojection median {Error:F1} px, focal {Focal:F0} px",
                    wallId, key.PanelId, key.Generation, e.Method, e.Used, e.Points, e.DistanceMm, c[2], e.MedianErrorPx, e.FocalPx);
            }
            else
            {
                logger.LogWarning(
                    "Footprints on wall {WallId}: photo {PanelId}/{Generation} has no camera ({Points} placed holds): {Reason}",
                    wallId, key.PanelId, key.Generation, e.Points, e.Rejection);
            }
        }
    }

    /// <summary>
    /// Stores the footprints on holds whose outline still matches; clears stale ones. A footprint that would be drawn off
    /// the hold's facet (<see cref="Wall3DHoldGuard.PlacementOnFacet"/>) is not stored, and clears the old one. Returns how many were written.
    /// </summary>
    private static async Task<int> WriteAsync(
        BlocwerkDbContext db, Guid wallId, HoldFootprintRefinement refinement, IReadOnlyDictionary<string, PlaneRectMm> extents, CancellationToken ct)
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

            var json = OnFacet(hold, fp, extents) ? fp.ToJson() : null;
            if (hold.FootprintMm != json)
            {
                hold.FootprintMm = json;
                written++;
            }
        }

        await db.SaveChangesAsync(ct);
        return written;
    }

    private static bool OnFacet(Hold hold, HoldFootprint fp, IReadOnlyDictionary<string, PlaneRectMm> extents) =>
        hold.FacetId is not { } facet || hold.PlaneAMm is not { } a || hold.PlaneBMm is not { } b
        || Wall3DHoldGuard.PlacementOnFacet(facet, a, b, extents, fp.Outline);
}
