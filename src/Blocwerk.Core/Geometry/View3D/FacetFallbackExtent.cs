// <copyright file="FacetFallbackExtent.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// The rectangle a facet without a solved <c>extentMm</c> is drawn with: the bounds of its marker corners and placed
/// holds, plus <see cref="Wall3DViewBuilder.FallbackMarginMm"/>. One rule for the 3D view and the ray casts onto it.
/// </summary>
public static class FacetFallbackExtent
{
    /// <summary>The fallback extent of a facet; null when it has neither markers nor placed holds.</summary>
    /// <param name="doc">The model.</param>
    /// <param name="facetId">The facet.</param>
    /// <param name="holds">The wall's live holds (only those placed on the facet count).</param>
    /// <returns>The rectangle, or null.</returns>
    public static PlaneRectMm? Of(WallGeometryDocument doc, string facetId, IEnumerable<Hold> holds)
    {
        var points = doc.Markers
            .Where(m => m.Facet == facetId)
            .SelectMany(m => m.CornersPlaneMm)
            .Where(c => c.Length >= 2)
            .Select(c => (c[0], c[1]))
            .Concat(holds
                .Where(h => h.FacetId == facetId && h.PlaneAMm.HasValue && h.PlaneBMm.HasValue)
                .Select(h => (h.PlaneAMm!.Value, h.PlaneBMm!.Value)));
        const double M = Wall3DViewBuilder.FallbackMarginMm;
        return PlaneRectMm.Bounds(points) is { } r ? new PlaneRectMm(r.AMin - M, r.AMax + M, r.BMin - M, r.BMax + M) : null;
    }
}
