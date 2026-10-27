// <copyright file="NeighbourPlaneFit.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// Where a photo point belongs on the model, judged from its nearest anchors in the same photo (points whose facet
/// position is trusted): the facet a clear majority of them sit on, and an affine photo → plane map fitted to the
/// nearest anchors on that facet. Local on purpose: a wide-angle photo of a steep wall is no single perspective map
/// (lens distortion, holds standing off the plane), but over a few neighbours an affine map holds. The worst
/// neighbour is dropped while it disagrees by more than the tolerance, so a few wrong anchors do not pull the fit.
/// </summary>
public static class NeighbourPlaneFit
{
    /// <summary>Anchors looked at around the point.</summary>
    public const int Neighbours = 8;

    /// <summary>Share of the neighbours that must sit on one facet before the point is put on it.</summary>
    public const double FacetMajority = 0.75;

    /// <summary>Fewest anchors an affine fit is made from (three define it; one more checks it).</summary>
    public const int MinFitAnchors = 4;

    /// <summary>The facet and map at (x, y), or null when the anchors are too few or split between facets.</summary>
    /// <param name="x">Normalised photo x.</param>
    /// <param name="y">Normalised photo y.</param>
    /// <param name="anchors">The photo's trusted points (never the point itself).</param>
    /// <param name="toleranceMm">A neighbour the fit misses by more than this is dropped (the worst first).</param>
    /// <returns>The facet and the local map, or null.</returns>
    public static (string FacetId, PhotoToPlane Map)? Predict(double x, double y, IReadOnlyList<PlaneAnchor> anchors, double toleranceMm)
    {
        var near = anchors.OrderBy(p => Distance(p, x, y)).ToList();
        var nearest = near.Take(Neighbours).ToList();
        if (nearest.Count < MinFitAnchors)
        {
            return null;
        }

        var majority = nearest.GroupBy(p => p.FacetId).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First();
        if (majority.Count() < FacetMajority * nearest.Count)
        {
            return null;
        }

        var fit = near.Where(p => p.FacetId == majority.Key).Take(Neighbours).ToList();
        var map = Trimmed(fit, toleranceMm);
        return map is null ? null : (majority.Key, map);
    }

    /// <summary>An affine fit, refitted without its worst anchor while that one is off by more than the tolerance.</summary>
    private static PhotoToPlane? Trimmed(List<PlaneAnchor> fit, double toleranceMm)
    {
        while (fit.Count >= MinFitAnchors && Affine(fit) is { } map)
        {
            var worst = fit.MaxBy(p => Miss(map, p))!;
            if (Miss(map, worst) <= toleranceMm || fit.Count == MinFitAnchors)
            {
                return map;
            }

            fit.Remove(worst);
        }

        return null;
    }

    /// <summary>Least-squares a = c0 + c1·x + c2·y (and b alike); null for a degenerate (collinear) layout.</summary>
    private static PhotoToPlane? Affine(IReadOnlyList<PlaneAnchor> pts)
    {
        // Normal equations, centred on the anchors' mean for conditioning.
        var (mx, my) = (pts.Average(p => p.X), pts.Average(p => p.Y));
        double sxx = 0, sxy = 0, syy = 0, sxa = 0, sya = 0, sxb = 0, syb = 0;
        foreach (var p in pts)
        {
            var (dx, dy) = (p.X - mx, p.Y - my);
            (sxx, sxy, syy) = (sxx + (dx * dx), sxy + (dx * dy), syy + (dy * dy));
            (sxa, sya, sxb, syb) = (sxa + (dx * p.A), sya + (dy * p.A), sxb + (dx * p.B), syb + (dy * p.B));
        }

        var det = (sxx * syy) - (sxy * sxy);
        if (Math.Abs(det) < 1e-12 * Math.Max(1e-12, sxx * syy))
        {
            return null;
        }

        var (ma, mb) = (pts.Average(p => p.A), pts.Average(p => p.B));
        var (ax, ay) = (((syy * sxa) - (sxy * sya)) / det, ((sxx * sya) - (sxy * sxa)) / det);
        var (bx, by) = (((syy * sxb) - (sxy * syb)) / det, ((sxx * syb) - (sxy * sxb)) / det);
        return (u, v) => (ma + (ax * (u - mx)) + (ay * (v - my)), mb + (bx * (u - mx)) + (by * (v - my)));
    }

    private static double Miss(PhotoToPlane map, PlaneAnchor p)
    {
        var (a, b) = map(p.X, p.Y);
        return Math.Sqrt(((a - p.A) * (a - p.A)) + ((b - p.B) * (b - p.B)));
    }

    private static double Distance(PlaneAnchor p, double x, double y) => ((p.X - x) * (p.X - x)) + ((p.Y - y) * (p.Y - y));
}
