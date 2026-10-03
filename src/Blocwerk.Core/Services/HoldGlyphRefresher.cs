// <copyright file="HoldGlyphRefresher.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.MarkerPlanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Re-places and re-measures holds a user just edited, in the same save, so an edit never drops a hold
/// out of the 3D view (see the rules on <c>Hold.Glyph.cs</c>). Per panel photo:
/// <list type="number">
/// <item>the photo's stored marker observations, measured exactly as ingest does (<see cref="HoldMetricPlanner"/>);</item>
/// <item>else the homography the 3D view fits to the photo's other placed holds (<see cref="HoldPlaneProjector"/>).</item>
/// </list>
/// A hold that is still placed (a reshape: its centre did not move) keeps its facet and plane position and
/// only gets its size back. Nothing is saved here: the caller commits.
/// </summary>
public static class HoldGlyphRefresher
{
    /// <summary>Refreshes every hold of <paramref name="holds"/> that is missing a placement or a size.</summary>
    /// <param name="db">The context the holds are tracked in.</param>
    /// <param name="holds">The edited holds (tracked, with their new geometry written).</param>
    /// <param name="logger">Where to warn about an unusable geometry model.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many holds got a placement or size back.</returns>
    public static async Task<int> RefreshAsync(
        BlocwerkDbContext db, IReadOnlyCollection<Hold> holds, ILogger logger, CancellationToken ct = default)
    {
        var refreshed = 0;
        foreach (var wall in holds.Where(h => h.NeedsGlyphRefresh()).GroupBy(h => h.WallId))
        {
            var doc = await HoldMetricPlanner.LoadActiveGeometryAsync(db, wall.Key, logger, ct);
            if (doc is null)
            {
                continue;
            }

            var revisions = await MarkerRevisionScope.LoadAsync(db, wall.Key, ct);
            foreach (var photo in wall.GroupBy(HoldPlaneProjector.PhotoOf))
            {
                refreshed += await RefreshPhotoAsync(db, doc, revisions, photo.Key, photo.ToList(), ct);
            }
        }

        return refreshed;
    }

    /// <summary>
    /// <see cref="RefreshAsync"/> for a user edit: a failure is logged, never thrown, so the edit itself
    /// still saves (the hold then shows as approximate in 3D and the refinement queue retries it).
    /// </summary>
    /// <param name="db">The context the holds are tracked in.</param>
    /// <param name="holds">The edited holds.</param>
    /// <param name="logger">Where to log.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many holds got a placement or size back.</returns>
    public static async Task<int> TryRefreshAsync(
        BlocwerkDbContext db, IReadOnlyCollection<Hold> holds, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            return await RefreshAsync(db, holds, logger, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not re-place {Count} edited holds; they are queued for refinement", holds.Count);
            return 0;
        }
    }

    private static async Task<int> RefreshPhotoAsync(
        BlocwerkDbContext db,
        WallGeometryDocument doc,
        MarkerRevisionScope revisions,
        Wall3DPhotoKey photo,
        List<Hold> holds,
        CancellationToken ct)
    {
        var markers = await PhotoMarkersAsync(db, revisions, photo, ct);
        var byMarkers = markers is null
            ? []
            : HoldMetricPlanner.Measure(
                holds, new Dictionary<Hold, HoldOutlineResult>(), markers.Markers, doc, null, (int)markers.Scale, (int)markers.Scale);

        HoldPlaneProjector? projector = null;
        var refreshed = 0;
        foreach (var hold in holds)
        {
            var placed = hold.FacetId is not null && hold.PlaneAMm is not null && hold.PlaneBMm is not null;
            var metric = byMarkers.GetValueOrDefault(hold);
            if (metric is null || (placed && metric.FacetId != hold.FacetId))
            {
                projector ??= await ProjectorAsync(db, doc, photo, holds, markers, ct);
                metric = FitMetric(hold, projector, doc, placed) ?? metric;
            }

            if (metric is null)
            {
                continue;
            }

            if (placed)
            {
                HoldMetricPlanner.ApplySize(hold, metric);
            }
            else
            {
                HoldMetricPlanner.Apply(hold, metric);
            }

            refreshed++;
        }

        return refreshed;
    }

    /// <summary>A placed hold is measured on its own facet; an unplaced one is placed first.</summary>
    private static HoldMetric? FitMetric(Hold hold, HoldPlaneProjector projector, WallGeometryDocument doc, bool placed)
    {
        if (placed)
        {
            return projector.For(hold) is { } mapping
                ? HoldFitMeasurer.Measure(hold, new HoldPlaneFit(hold.FacetId!, hold.PlaneAMm!.Value, hold.PlaneBMm!.Value, mapping.Map, mapping.Source))
                : null;
        }

        return projector.Place(hold, Wall3DFallbackPlacement.FacetExtents(doc)) is { } fit ? HoldFitMeasurer.Measure(hold, fit) : null;
    }

    /// <summary>The photo's stored marker observations usable with the active model, or null when it has none.</summary>
    private static async Task<Wall3DPhotoMarkers?> PhotoMarkersAsync(
        BlocwerkDbContext db, MarkerRevisionScope revisions, Wall3DPhotoKey photo, CancellationToken ct)
    {
        var rows = await db.WallMarkerObservations.AsNoTracking()
            .Where(o => o.WallPanelId == photo.PanelId && o.PanelGeneration == photo.Generation)
            .ToListAsync(ct);
        rows = await revisions.FilterAsync(rows, ct);
        return rows.Count == 0 ? null : Wall3DPhotoMarkerLoader.ToPhoto(rows);
    }

    /// <summary>
    /// The 3D view's projector for this photo: its other placed holds as they are stored, plus the edited
    /// holds that are still placed (their in-memory position is the stored one).
    /// </summary>
    private static async Task<HoldPlaneProjector> ProjectorAsync(
        BlocwerkDbContext db,
        WallGeometryDocument doc,
        Wall3DPhotoKey photo,
        List<Hold> edited,
        Wall3DPhotoMarkers? markers,
        CancellationToken ct)
    {
        var editedIds = edited.Select(h => h.Id).ToList();
        var peers = await db.Holds.AsNoTracking()
            .Where(h => h.WallPanelId == photo.PanelId && h.Generation == photo.Generation && !h.IsVirtual
                && h.FacetId != null && h.PlaneAMm != null && h.PlaneBMm != null && !editedIds.Contains(h.Id))
            .ToListAsync(ct);
        peers.AddRange(edited.Where(h => h.FacetId is not null && h.PlaneAMm is not null && h.PlaneBMm is not null));
        var photos = markers is null ? null : new Dictionary<Wall3DPhotoKey, Wall3DPhotoMarkers> { [photo] = markers };
        return HoldPlaneProjector.Create(peers, doc, photos);
    }
}
