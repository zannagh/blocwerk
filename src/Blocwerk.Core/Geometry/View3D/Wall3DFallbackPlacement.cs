// <copyright file="Wall3DFallbackPlacement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// The 3D view's last resort for a live hold without a stored facet position: place it for this view only
/// through its panel photo's projector (<see cref="HoldPlaneProjector.Place"/>), size it from its outline
/// through the same mapping, and flag it <see cref="Wall3DHold.PlacementApproximate"/>. Never stored.
/// </summary>
public static class Wall3DFallbackPlacement
{
    /// <summary>The model's known facet extents, by facet id.</summary>
    /// <param name="doc">The model.</param>
    /// <returns>The extents.</returns>
    public static Dictionary<string, PlaneRectMm> FacetExtents(WallGeometryDocument doc) =>
        doc.Segments.SelectMany(s => s.Facets)
            .Where(f => !string.IsNullOrEmpty(f.Id) && f.ExtentMm is { Area: > 0 })
            .GroupBy(f => f.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().ExtentMm!.Value, StringComparer.Ordinal);

    /// <summary>Places the hold, or null when it has no panel photo or the photo maps onto no known facet.</summary>
    /// <param name="hold">An unplaced live hold.</param>
    /// <param name="projector">The wall's projector.</param>
    /// <param name="extents">Known facet extents.</param>
    /// <param name="frames">The model's facet frames.</param>
    /// <returns>The placement and its facet frame, or null.</returns>
    public static (HoldPlaneFit Fit, FacetFrame Frame)? Place(
        Hold hold,
        HoldPlaneProjector projector,
        IReadOnlyDictionary<string, PlaneRectMm> extents,
        IReadOnlyDictionary<string, FacetFrame> frames)
    {
        if (hold.WallPanelId is null || hold.IsVirtual)
        {
            return null;
        }

        return projector.Place(hold, extents) is { } fit && frames.TryGetValue(fit.FacetId, out var frame)
            ? (fit, frame)
            : null;
    }

    /// <summary>The hold as drawn: its outline mapped through the placement, sized from it when unmeasured.</summary>
    /// <param name="hold">The hold.</param>
    /// <param name="fit">Its placement.</param>
    /// <param name="placed">The hold built at that placement with the stored (or default) size.</param>
    /// <returns>The hold to draw, flagged approximate.</returns>
    public static Wall3DHold Draw(Hold hold, HoldPlaneFit fit, Wall3DHold placed)
    {
        if (!placed.SizeMeasured && HoldFitMeasurer.Measure(hold, fit) is { WidthMm: > 0, HeightMm: > 0 } metric)
        {
            placed = placed with { WidthMm = metric.WidthMm, HeightMm = metric.HeightMm, SizeMeasured = true };
        }

        var shape = HoldShapeProjector.Project(hold, placed.WidthMm, placed.HeightMm, (fit.Map, fit.Source));
        return placed with { Shape = shape, PlacementApproximate = true };
    }
}
