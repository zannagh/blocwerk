// <copyright file="InlierSupport.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// Where a registration is supported: its inliers mapped onto the facet plane and their convex hull. A hold placed
/// inside the hull, near inliers, is interpolated; one far beyond it is extrapolated, where a small error of the fit
/// grows into centimetres. Pure.
/// </summary>
public sealed class InlierSupport
{
    /// <summary>Extrapolation beyond the inlier hull, as a share of the facet's longer side, that no longer counts as supported.</summary>
    public const double MaxExtrapolationShare = 0.15;

    private readonly List<(double X, double Y)> points;
    private readonly List<(double X, double Y)> hull;

    private InlierSupport(List<(double X, double Y)> points, PlaneRectMm extent)
    {
        this.points = points;
        hull = PhotoCoverage.ConvexHull(points);
        ExtrapolationLimitMm = MaxExtrapolationShare * Math.Max(extent.AMax - extent.AMin, extent.BMax - extent.BMin);
    }

    /// <summary>Gets the distance beyond the hull above which a placement counts as extrapolated, mm.</summary>
    public double ExtrapolationLimitMm { get; }

    /// <summary>The support of an accepted registration, or null when it has no fit or no known inliers.</summary>
    /// <param name="registration">The registration.</param>
    /// <returns>The support.</returns>
    public static InlierSupport? Of(FacetRegistration registration)
    {
        if (!registration.Accepted || registration.PhotoToPlane is null || registration.InlierPoints is not { Count: > 0 } inliers)
        {
            return null;
        }

        var mapped = inliers.Select(p => registration.Map(p.X, p.Y)).Where(p => double.IsFinite(p.A) && double.IsFinite(p.B)).ToList();
        return mapped.Count == 0 ? null : new InlierSupport(mapped, registration.Extent);
    }

    /// <summary>The distance from plane point (a, b) to the nearest inlier, mm (the hold's local residual scale).</summary>
    /// <param name="a">Plane a, mm.</param>
    /// <param name="b">Plane b, mm.</param>
    /// <returns>The distance.</returns>
    public double NearestInlierMm(double a, double b) => Math.Sqrt(points.Min(p => Sq(p.X - a) + Sq(p.Y - b)));

    /// <summary>How far plane point (a, b) lies beyond the inliers' hull, mm (0 inside it).</summary>
    /// <param name="a">Plane a, mm.</param>
    /// <param name="b">Plane b, mm.</param>
    /// <returns>The distance.</returns>
    public double BeyondHullMm(double a, double b)
    {
        if (hull.Count >= 3 && PhotoCoverage.Inside(hull, a, b))
        {
            return 0;
        }

        if (hull.Count == 1)
        {
            return Math.Sqrt(Sq(hull[0].X - a) + Sq(hull[0].Y - b));
        }

        var best = double.PositiveInfinity;
        for (var i = 0; i < hull.Count; i++)
        {
            best = Math.Min(best, SegmentDistance(hull[i], hull[(i + 1) % hull.Count], a, b));
        }

        return best;
    }

    /// <summary>Whether plane point (a, b) is extrapolated beyond <see cref="ExtrapolationLimitMm"/>.</summary>
    /// <param name="a">Plane a, mm.</param>
    /// <param name="b">Plane b, mm.</param>
    /// <returns>True when it is.</returns>
    public bool IsExtrapolated(double a, double b) => BeyondHullMm(a, b) > ExtrapolationLimitMm;

    private static double SegmentDistance((double X, double Y) p, (double X, double Y) q, double a, double b)
    {
        var (dx, dy) = (q.X - p.X, q.Y - p.Y);
        var len = Sq(dx) + Sq(dy);
        var t = len < 1e-12 ? 0 : Math.Clamp((((a - p.X) * dx) + ((b - p.Y) * dy)) / len, 0, 1);
        return Math.Sqrt(Sq(p.X + (t * dx) - a) + Sq(p.Y + (t * dy) - b));
    }

    private static double Sq(double v) => v * v;
}
