using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Detection.Enrichment;

/// <summary>The glyph (marker) half of the enrichment: glyph walls only.</summary>
public sealed partial class HoldEnrichmentService
{
    private async Task PlanMarkersAsync(
        BlocwerkDbContext db,
        HoldEnrichmentRequest request,
        HoldEnrichmentPlan plan,
        (int Width, int Height)? outlineImageSize,
        CancellationToken ct)
    {
        // Markers are opt-in per wall: detection never even runs on a wall without the flag.
        if (!settings.MarkersEnabled || markerService is null || !request.Wall.GlyphsEnabled)
        {
            return;
        }

        // The wall's plan says which ids are real (and how big each is); without one: the legacy defaults.
        var layout = WallMarkerLayoutResolver.Resolve(
            await WallMarkerLayoutResolver.CurrentPlanJsonAsync(db, request.Wall.Id, ct), request.Wall.MarkerSizeMm);
        var options = layout.IsFromPlan ? layout.DetectionOptions : null;
        var detection = await Task.Run(() => markerService.DetectAsync(request.Image, options, ct), ct);
        plan.MarkerPassRan = true;
        plan.MarkerCount = detection.Markers.Count;
        await PlanObservationsAsync(db, request, detection, plan, ct);
        var holds = PlanMarkerHoldDrops(request.Holds, detection.Markers, plan);

        var document = await LoadActiveGeometryAsync(db, request.Wall.Id, ct);
        var width = outlineImageSize?.Width ?? detection.ImageWidth;
        var height = outlineImageSize?.Height ?? detection.ImageHeight;
        PlanMetrics(request, holds, detection.Markers, document, plan, (width, height), layout);
    }

    /// <summary>
    /// Plans dropping the auto-detected holds that are really the printed marker sheets (see
    /// <see cref="MarkerHoldFilter"/>), forgets their outlines, and returns the holds that stay.
    /// </summary>
    private static IReadOnlyList<Hold> PlanMarkerHoldDrops(
        IReadOnlyList<Hold> holds, IReadOnlyList<DetectedMarker> markers, HoldEnrichmentPlan plan)
    {
        var onMarkers = MarkerHoldFilter.HoldsOnMarkers(holds, markers);
        if (onMarkers.Count == 0)
        {
            return holds;
        }

        plan.MarkerHolds.AddRange(onMarkers);
        foreach (var hold in onMarkers)
        {
            plan.Outlines.Remove(hold);
        }

        var dropped = onMarkers.ToHashSet();
        return holds.Where(h => !dropped.Contains(h)).ToList();
    }

    private static void PlanMetrics(
        HoldEnrichmentRequest request,
        IReadOnlyList<Hold> holds,
        IReadOnlyList<DetectedMarker> markers,
        WallGeometryDocument? document,
        HoldEnrichmentPlan plan,
        (int Width, int Height) size,
        WallMarkerLayout layout)
    {
        var metrics = HoldMetricPlanner.Measure(
            holds, plan.Outlines, markers, document, request.Wall.MarkerSizeMm, size.Width, size.Height, layout);
        foreach (var (hold, metric) in metrics)
        {
            plan.Metrics[hold] = metric;
        }
    }

    /// <summary>
    /// Replaces the observation rows of this exact panel photo (panel + generation + staged/live). A
    /// legacy single-image upload has no panel to key them on and stores none.
    /// </summary>
    private static async Task PlanObservationsAsync(
        BlocwerkDbContext db,
        HoldEnrichmentRequest request,
        MarkerDetectionResult detection,
        HoldEnrichmentPlan plan,
        CancellationToken ct)
    {
        if (request.PanelId is not { } panelId)
        {
            return;
        }

        plan.StaleObservations.AddRange(await db.WallMarkerObservations
            .Where(o => o.WallPanelId == panelId
                        && o.PanelGeneration == request.PanelGeneration
                        && o.FromStagedPhoto == request.FromStagedPhoto)
            .ToListAsync(ct));

        var now = DateTimeOffset.UtcNow;
        plan.NewObservations.AddRange(detection.Markers.Select(m => new WallMarkerObservation
        {
            WallPanelId = panelId,
            PanelGeneration = request.PanelGeneration,
            FromStagedPhoto = request.FromStagedPhoto,
            MarkerId = m.Id,
            CornersJson = JsonSerializer.Serialize(m.CornersNormalized.Select(c => new[] { c.X, c.Y })),
            SidePx = m.SidePx,
            Synthetic = m.Synthetic,
            DetectedAt = now,
        }));
    }

    /// <summary>The wall's active geometry model, or null (none, unparseable, or a newer schema).</summary>
    private Task<WallGeometryDocument?> LoadActiveGeometryAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct) =>
        HoldMetricPlanner.LoadActiveGeometryAsync(db, wallId, logger, ct);
}
