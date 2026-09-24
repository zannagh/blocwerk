// <copyright file="HoldFitMeasurer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Detection.Enrichment;

/// <summary>
/// Measures a hold through a <see cref="HoldPlaneFit"/> (a photo → facet mapping the 3D view also uses)
/// the same way <see cref="HoldMetricMeasurer"/> measures through a marker fit: the outline's bounding
/// box along the facet's u/v, its area minus its pocket holes, and the mapped centre. Works in the
/// photo's normalised coordinates, so a plain circle is measured as a circle of the normalised radius.
/// </summary>
public static class HoldFitMeasurer
{
    /// <summary>The measurement, or null when the mapping is not finite on the outline.</summary>
    /// <param name="hold">The hold (its current centre and outline).</param>
    /// <param name="fit">Where the projector placed it.</param>
    /// <returns>The metric (source <see cref="HoldMetric.HoldFit"/> unless the fit came from markers).</returns>
    public static HoldMetric? Measure(Hold hold, HoldPlaneFit fit)
    {
        var plane = HoldMetricMeasurer.OutlinePx(hold, null, 1, 1).Select(p => fit.Map(p.X, p.Y)).ToList();
        if (plane.Count < 3 || !plane.All(IsFinite) || !double.IsFinite(fit.PlaneAMm) || !double.IsFinite(fit.PlaneBMm))
        {
            return null;
        }

        var holeArea = HoldMetricMeasurer.HolesPx(hold, null, 1, 1)
            .Select(ring => ring.Select(p => fit.Map(p.X, p.Y)).ToList())
            .Where(ring => ring.Count >= 3 && ring.All(IsFinite))
            .Sum(HoldMetricMeasurer.ShoelaceArea);
        var source = fit.Source == Wall3DShapeSource.Markers ? HoldMetric.MultiMarker : HoldMetric.HoldFit;
        return new HoldMetric(
            plane.Max(p => p.A) - plane.Min(p => p.A),
            plane.Max(p => p.B) - plane.Min(p => p.B),
            Math.Max(0, HoldMetricMeasurer.ShoelaceArea(plane) - holeArea),
            fit.FacetId,
            fit.PlaneAMm,
            fit.PlaneBMm,
            source);
    }

    private static bool IsFinite((double A, double B) p) => double.IsFinite(p.A) && double.IsFinite(p.B);
}
