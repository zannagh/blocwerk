// <copyright file="HoldTexturePlacer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// Places one hold of a registered photo: every accepted facet registration maps the hold's centre onto its
/// plane, and a facet qualifies when the centre lands inside its extent. Among several, the hold's own facet
/// wins (the rule <see cref="HoldPlaneProjector.Place"/> uses), else the registration with the most inliers.
/// The size is measured through the same mapping by <see cref="HoldFitMeasurer"/>.
/// </summary>
public static class HoldTexturePlacer
{
    /// <summary>
    /// Tolerance around a facet's extent. A hold on the extent's edge maps a few mm either side of it with any
    /// fit error; without the margin it would fall off both facets of a fold.
    /// </summary>
    public const double ExtentMarginMm = 25;

    /// <summary>Whether a hold may be (re)placed: nothing but this action placed it so far.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>True for no metric source or a texture-registration one (registered or carried over).</returns>
    public static bool IsEligible(Hold hold) =>
        !hold.IsVirtual && hold.WallPanelId is not null && (hold.MetricSource is null || IsTexturePlaced(hold));

    /// <summary>Whether this action placed the hold: registered, or carried over from an earlier model.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>True for either texture-registration metric source.</returns>
    public static bool IsTexturePlaced(Hold hold) =>
        hold.MetricSource is HoldMetric.TextureRegistration or HoldMetric.TextureRegistrationCarried;

    /// <summary>The placement, or null when no accepted facet contains the hold's centre.</summary>
    /// <param name="hold">The hold (its normalised centre on its panel photo).</param>
    /// <param name="registrations">The photo's registrations; rejected ones are ignored.</param>
    /// <returns>The placement.</returns>
    public static HoldPlaneFit? Place(Hold hold, IEnumerable<FacetRegistration> registrations)
    {
        HoldPlaneFit? best = null;
        var bestInliers = -1;
        foreach (var r in registrations.Where(r => r.Accepted))
        {
            var (a, b) = r.Map(hold.X, hold.Y);
            if (!double.IsFinite(a) || !double.IsFinite(b) || !Inside(r.Extent, a, b))
            {
                continue;
            }

            var fit = new HoldPlaneFit(r.FacetId, a, b, r.Map, Wall3DShapeSource.HoldFit);
            if (r.FacetId == hold.FacetId)
            {
                return fit;
            }

            if (r.Inliers > bestInliers)
            {
                best = fit;
                bestInliers = r.Inliers;
            }
        }

        return best;
    }

    /// <summary>The hold's size through its placement, tagged <see cref="HoldMetric.TextureRegistration"/>; null when unmeasurable.</summary>
    /// <param name="hold">The hold.</param>
    /// <param name="fit">Its placement.</param>
    /// <returns>The metric.</returns>
    public static HoldMetric? Measure(Hold hold, HoldPlaneFit fit) =>
        HoldFitMeasurer.Measure(hold, fit) is { WidthMm: > 0, HeightMm: > 0 } metric
            ? metric with { MetricSource = HoldMetric.TextureRegistration }
            : null;

    private static bool Inside(PlaneRectMm r, double a, double b) =>
        a >= r.AMin - ExtentMarginMm && a <= r.AMax + ExtentMarginMm
        && b >= r.BMin - ExtentMarginMm && b <= r.BMax + ExtentMarginMm;
}
