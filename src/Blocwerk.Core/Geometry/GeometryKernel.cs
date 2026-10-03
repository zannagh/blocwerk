// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Geometry;

/// <summary>
/// The wall geometry kernel's rules and constants, shared with the Python wall-geometry service (<c>docs/geometry-kernel.md</c>).
/// Where a facet really is (its shape), which side of a seam it keeps, which markers say so and when a line of sight counts
/// as blocked are one rule in both languages: the Python twin is <c>docker/wall-geometry/wallgeometry/kernel.py</c>, and
/// <c>test/geometry-golden/*.json</c> holds the cases both must answer the same way. Change a value here only together
/// with the Python twin and the doc.
/// </summary>
public static class GeometryKernel
{
    /// <summary>Planes closer to parallel than this (sine of their angle, ~9.8°) meet in no reliable seam line.</summary>
    public const double MinPlaneAngleSin = 0.17;

    /// <summary>A seam cuts a facet only where the neighbour really is: the seam runs, on average, within this of its region, mm.</summary>
    public const double MaxSeamGapMm = 800;

    /// <summary>A marker corner this close to a seam still counts as on either side of it, mm.</summary>
    public const double MarkerSideTolMm = 20;

    /// <summary>
    /// Only markers whose centre lies within the facet's extent grown by this vote on its seam sides and its marker centroid,
    /// mm: a marker the solver left out of the extent (a stray on a coplanar neighbour) says nothing about the facet's shape.
    /// </summary>
    public const double MarkerVoteMarginMm = 50;

    /// <summary>A target this close to an occluder's plane is on that occluder's seam: the occluder does not block it, mm.</summary>
    public const double NearPlaneMm = 30;

    /// <summary>A camera this close to an occluder's plane does not cross it, mm.</summary>
    public const double OnPlaneMm = 1e-6;

    /// <summary>A view counts only from the front of the surface: cos(view ray, outward normal) above this (~87°).</summary>
    public const double MinFacingCos = 0.05;

    /// <summary>Outline edges this close to the extent rectangle's sides are the rectangle itself, not a fold clip, mm.</summary>
    public const double OutlineEdgeTolMm = 1;

    /// <summary>
    /// The plane corners [a, b] of the markers that vote on the facet's seam sides: its own markers whose centre lies within
    /// its extent grown by <see cref="MarkerVoteMarginMm"/> (all of its markers when it has no extent).
    /// </summary>
    /// <param name="doc">The model.</param>
    /// <param name="facetId">The facet.</param>
    /// <param name="extent">The facet's extent, or null.</param>
    /// <returns>The voting corners.</returns>
    public static List<double[]> VotingCorners(WallGeometryDocument doc, string facetId, PlaneRectMm? extent)
    {
        var result = new List<double[]>();
        foreach (var marker in doc.Markers.Where(m => m.Facet == facetId))
        {
            var corners = marker.CornersPlaneMm.Where(c => c.Length >= 2).ToList();
            if (corners.Count == 0)
            {
                continue;
            }

            double a = corners.Average(c => c[0]), b = corners.Average(c => c[1]), g = MarkerVoteMarginMm;
            if (extent is { Area: > 0 } e && (a < e.AMin - g || a > e.AMax + g || b < e.BMin - g || b > e.BMax + g))
            {
                continue;
            }

            result.AddRange(corners);
        }

        return result;
    }

    /// <summary>
    /// The fold clips of a facet's <see cref="WallGeometryFacet.OutlineMm"/> (a convex polygon in its (a, b); SfM models)
    /// as unit half-planes, kept where alpha·a + beta·b ≥ gamma. Edges along the extent rectangle's sides are the
    /// rectangle itself and are skipped.
    /// </summary>
    /// <param name="outline">The outline, [a, b] mm, or null.</param>
    /// <param name="extent">The facet's extent, or null.</param>
    /// <returns>The half-planes (none without an outline).</returns>
    public static List<(double Alpha, double Beta, double Gamma)> OutlineHalfPlanes(IReadOnlyList<double[]>? outline, PlaneRectMm? extent)
    {
        var result = new List<(double, double, double)>();
        var p = outline?.Where(c => c.Length >= 2).ToList() ?? [];
        if (p.Count < 3)
        {
            return result;
        }

        var area = p.Select((q, i) => (q[0] * p[(i + 1) % p.Count][1]) - (p[(i + 1) % p.Count][0] * q[1])).Sum() / 2;
        if (Math.Abs(area) < 1e-9)
        {
            return result;
        }

        for (var i = 0; i < p.Count; i++)
        {
            var s = p[i];
            var t = p[(i + 1) % p.Count];
            double da = t[0] - s[0], db = t[1] - s[1];
            var length = Math.Sqrt((da * da) + (db * db));
            if (length < OutlineEdgeTolMm || (extent is { } e && OnRectSide(s, t, e)))
            {
                continue;
            }

            // inward normal of a counter-clockwise (area > 0) or clockwise ring
            double alpha = (area > 0 ? -db : db) / length, beta = (area > 0 ? da : -da) / length;
            result.Add((alpha, beta, (alpha * s[0]) + (beta * s[1])));
        }

        return result;
    }

    /// <summary>Whether a camera at <paramref name="camera"/> sees <paramref name="point"/> from the front of a surface with unit <paramref name="normal"/>.</summary>
    /// <param name="point">The surface point, world mm.</param>
    /// <param name="normal">Its outward unit normal.</param>
    /// <param name="camera">The camera centre, world mm.</param>
    /// <returns>True when facing.</returns>
    public static bool Facing(double[] point, double[] normal, double[] camera)
    {
        double[] v = [camera[0] - point[0], camera[1] - point[1], camera[2] - point[2]];
        var dist = Math.Sqrt((v[0] * v[0]) + (v[1] * v[1]) + (v[2] * v[2]));
        return ((v[0] * normal[0]) + (v[1] * normal[1]) + (v[2] * normal[2])) / Math.Max(dist, 1e-9) > MinFacingCos;
    }

    private static bool OnRectSide(double[] s, double[] t, PlaneRectMm e)
    {
        bool Both(int k, double x) => Math.Abs(s[k] - x) <= OutlineEdgeTolMm && Math.Abs(t[k] - x) <= OutlineEdgeTolMm;
        return Both(0, e.AMin) || Both(0, e.AMax) || Both(1, e.BMin) || Both(1, e.BMax);
    }
}
