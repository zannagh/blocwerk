// <copyright file="PhotoConsistency.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// How well one panel photo's stored hold placements agree with each other: per facet, one robust perspective map from
/// the holds' photo centres to their plane positions (<see cref="RobustHomographyFitter"/>, as
/// <see cref="HoldPlaneProjector"/> fits it), and the 90th percentile of every placed hold's distance from it. A photo
/// whose holds were placed well scores a few centimetres; one with many contradicting placements scores far more.
/// </summary>
public static class PhotoConsistency
{
    /// <summary>The photo's score in mm (lower is better); <see cref="double.PositiveInfinity"/> when no facet has enough holds.</summary>
    /// <param name="placed">The photo's placed holds (facet and plane position set).</param>
    /// <returns>The 90th-percentile residual.</returns>
    public static double P90ResidualMm(IEnumerable<Hold> placed)
    {
        var residuals = new List<double>();
        foreach (var facet in placed.GroupBy(h => h.FacetId!, StringComparer.Ordinal))
        {
            var holds = facet.OrderBy(h => h.Id).ToList();
            if (holds.Count < HoldPlaneProjector.MinHoldsForFit)
            {
                continue;
            }

            var pairs = holds.Select(h => new PointCorrespondence(h.X, h.Y, h.PlaneAMm!.Value, h.PlaneBMm!.Value)).ToList();
            if (RobustHomographyFitter.Fit(pairs, 4, HoldPlaneProjector.HoldFitThresholdMm) is not { } fit)
            {
                continue;
            }

            residuals.AddRange(pairs.Select(p => Math.Sqrt(RobustHomographyFitter.ErrorSq(fit.Homography, p))));
        }

        if (residuals.Count == 0)
        {
            return double.PositiveInfinity;
        }

        residuals.Sort();
        return residuals[(int)(0.9 * (residuals.Count - 1))];
    }
}
