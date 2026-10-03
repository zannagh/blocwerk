// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Cuts a triangular segment's facet to its shape. The marker plan says WHICH segments are right triangles, which
/// parent their hypotenuse lies against and where their right angle is (its millimetres need not match the solve);
/// the hypotenuse itself is where the facet's solved plane meets the parent facet's. The facet's extent rectangle is
/// clipped by that line, keeping the half with the plan's right-angle corner (<see cref="TriangleKeptSide"/>): a side
/// wall under an overhang reaches down to the floor, a closing piece fills the space behind the slope. When the parent's
/// seam cuts nothing (the plan's parent need not be the facet the hypotenuse rests on), the seam with the nearest other
/// facet that does cut it is used instead; that cut is not the plan's hypotenuse, so its corner is not trusted there.
/// </summary>
public static class Wall3DFacetOutlines
{
    /// <summary>Planes closer to parallel than this (sine of their angle, ~10°) give no reliable seam.</summary>
    public const double MinPlaneAngleSin = GeometryKernel.MinPlaneAngleSin;

    /// <summary>A seam must cut at least this share of the extent off; less is a sliver at an edge, not a hypotenuse.</summary>
    public const double MinCutFraction = 0.15;

    /// <summary>A non-parent facet's seam counts only when it lies, on average, within this of that facet's extent.</summary>
    public const double MaxSeamGapMm = GeometryKernel.MaxSeamGapMm;

    /// <summary>Outline corners closer than this are merged.</summary>
    public const double MinEdgeMm = 10;

    /// <summary>A clipped outline becomes a right triangle when that adds at most this share of its area.</summary>
    public const double MaxTriangleGrowth = 0.1;

    /// <summary>Triangle segment index → the parent its hypotenuse is attached to and its right-angle corner.</summary>
    public static IReadOnlyDictionary<int, PlanTriangle> PlanTriangles(MarkerPlan? plan) =>
        plan?.Segments
            .Where(s => s.Shape == SegmentShape.Triangle && s.AttachedTo is { OwnEdge: SegmentEdge.Hypotenuse })
            .ToDictionary(s => s.Index, s => new PlanTriangle(s.AttachedTo!.ParentIndex, s.RightAngle))
        ?? new Dictionary<int, PlanTriangle>();

    /// <summary>
    /// The facets, each triangle's with its clipped <see cref="Wall3DFacet.Outline"/> where one can be found and reaching
    /// the floor where its plan says so (<see cref="FacetFloorReach"/>), any other facet with the solver's fold-clipped
    /// outline (<see cref="WallGeometryFacet.OutlineMm"/>, SfM models) with that one, then every outline trimmed where it
    /// pokes a little through a neighbour's plane at their seam (<see cref="FacetSeamTrim"/>).
    /// </summary>
    public static List<Wall3DFacet> Apply(List<Wall3DFacet> facets, WallGeometryDocument doc, IReadOnlyDictionary<int, PlanTriangle>? triangles)
    {
        if (triangles is { Count: > 0 })
        {
            var cut = facets.Select(f => WithOutline(f, facets, doc, triangles)).ToList();
            facets = FacetFloorReach.Apply(cut, doc, triangles);
        }

        facets = facets.Select(f => f.Outline is null && SolvedOutline(doc, f.Id) is { } o ? WithShape(f, o) : f).ToList();
        return FacetSeamTrim.Apply(facets, doc);
    }

    /// <summary>
    /// The facet's extent clipped where its plane meets <paramref name="other"/>'s, keeping the half with the
    /// <paramref name="rightAngle"/> corner, else the side behind <paramref name="other"/>, else the side of
    /// <paramref name="centroid"/> ([a, b] mm; <see cref="TriangleKeptSide"/>). Null when the clip is unreliable or
    /// either side is less than <see cref="MinCutFraction"/> of the extent. A clip that only leaves stubs of the
    /// extent's margin at the hypotenuse's ends is completed to the right triangle (<see cref="MaxTriangleGrowth"/>).
    /// </summary>
    public static IReadOnlyList<double[]>? Clip(
        Wall3DFacet facet, Wall3DFacet other, (double A, double B)? centroid, TriangleCorner? rightAngle = null)
    {
        if (SeamSide(facet, other) is not { } side
            || TriangleKeptSide.Sign(side, facet.Extent, rightAngle, other.Normal, centroid) is not { } sign)
        {
            return null;
        }

        double KeepSide(double a, double b) => sign * side(a, b);
        var outline = ClipRect(facet.Extent, KeepSide);
        var e = facet.Extent;
        var share = outline is null ? 1 : Area(outline) / ((e.AMax - e.AMin) * (e.BMax - e.BMin));
        return share >= MinCutFraction && share <= 1 - MinCutFraction ? AsTriangle(outline!, KeepSide) : null;
    }

    /// <summary>
    /// The right triangle with its legs along the extent's edges through the outline's corner farthest from the
    /// seam and its hypotenuse on the seam; the outline itself when already a triangle or when that grows it too much.
    /// </summary>
    public static IReadOnlyList<double[]> AsTriangle(IReadOnlyList<double[]> outline, Func<double, double, double> side)
    {
        var s0 = side(0, 0);
        var ka = side(1, 0) - s0;
        var kb = side(0, 1) - s0;
        if (outline.Count <= 3 || Math.Abs(ka) < 1e-3 || Math.Abs(kb) < 1e-3)
        {
            return outline;
        }

        var c = outline.MaxBy(p => side(p[0], p[1]))!;
        List<double[]> triangle = [[-(s0 + (kb * c[1])) / ka, c[1]], c, [c[0], -(s0 + (ka * c[0])) / kb]];
        if (SignedArea(triangle) < 0)
        {
            triangle.Reverse();
        }

        return SignedArea(triangle) <= (1 + MaxTriangleGrowth) * Area(outline) ? triangle : outline;
    }

    /// <summary>
    /// Mean distance (mm) of the seam — <paramref name="outline"/>'s edge on <paramref name="other"/>'s plane — from
    /// <paramref name="other"/>'s extent, measured in its plane; infinity when the outline has no such edge.
    /// </summary>
    public static double SeamGapMm(Wall3DFacet facet, Wall3DFacet other, IReadOnlyList<double[]> outline)
    {
        var side = SeamSide(facet, other);
        var onSeam = outline.Where(p => side is not null && Math.Abs(side(p[0], p[1])) < 2 * MinEdgeMm).ToList();
        if (onSeam.Count < 2)
        {
            return double.PositiveInfinity;
        }

        const int Steps = 10;
        var (s, t) = onSeam.SelectMany(p => onSeam.Select(q => (p, q)))
            .MaxBy(pq => Math.Abs(pq.p[0] - pq.q[0]) + Math.Abs(pq.p[1] - pq.q[1]));
        var total = 0.0;
        for (var i = 0; i <= Steps; i++)
        {
            var w = World(facet, s[0] + ((t[0] - s[0]) * i / Steps), s[1] + ((t[1] - s[1]) * i / Steps));
            double[] d = [w[0] - other.Origin[0], w[1] - other.Origin[1], w[2] - other.Origin[2]];
            total += RectDistance(other.Extent, Dot(d, other.U), Dot(d, other.V));
        }

        return total / (Steps + 1);
    }

    /// <summary>The rectangle's part where <paramref name="side"/> ≥ 0; null when that is all of it or less than a triangle.</summary>
    public static IReadOnlyList<double[]>? ClipRect(PlaneRectMm rect, Func<double, double, double> side)
    {
        var corners = RectCorners(rect);
        return corners.All(c => side(c[0], c[1]) >= 0) ? null : ClipHalf(corners, side);
    }

    /// <summary>The rectangle's corners (aMin,bMin), (aMax,bMin), (aMax,bMax), (aMin,bMax).</summary>
    public static IReadOnlyList<double[]> RectCorners(PlaneRectMm r) =>
        [[r.AMin, r.BMin], [r.AMax, r.BMin], [r.AMax, r.BMax], [r.AMin, r.BMax]];

    /// <summary>The convex polygon's part where <paramref name="side"/> ≥ 0; null when less than a triangle is left.</summary>
    public static IReadOnlyList<double[]>? ClipHalf(IReadOnlyList<double[]> polygon, Func<double, double, double> side)
    {
        var result = new List<double[]>();
        for (var i = 0; i < polygon.Count; i++)
        {
            var p = polygon[i];
            var q = polygon[(i + 1) % polygon.Count];
            var sp = side(p[0], p[1]);
            var sq = side(q[0], q[1]);
            if (sp >= 0)
            {
                AddDistinct(result, p);
            }

            if ((sp >= 0) != (sq >= 0))
            {
                var t = sp / (sp - sq);
                AddDistinct(result, [p[0] + (t * (q[0] - p[0])), p[1] + (t * (q[1] - p[1]))]);
            }
        }

        if (result.Count > 1 && Same(result[0], result[^1]))
        {
            result.RemoveAt(result.Count - 1);
        }

        return result.Count >= 3 ? result : null;
    }

    /// <summary>The facet with <paramref name="outline"/> as its outline and corners.</summary>
    internal static Wall3DFacet WithShape(Wall3DFacet facet, IReadOnlyList<double[]> outline) =>
        facet with { Corners = outline.Select(p => World(facet, p[0], p[1])).ToList(), Outline = outline };

    /// <summary>Signed distance (mm) in the facet's plane from the line where it meets <paramref name="other"/>'s plane.</summary>
    internal static Func<double, double, double>? SeamSide(Wall3DFacet facet, Wall3DFacet other)
    {
        var n = PlaneNormal(other);
        var alpha = Dot(n, facet.U);
        var beta = Dot(n, facet.V);
        var norm = Math.Sqrt((alpha * alpha) + (beta * beta));
        if (norm < MinPlaneAngleSin)
        {
            return null;
        }

        var gamma = Dot(n, other.Origin) - Dot(n, facet.Origin);
        return (a, b) => ((alpha * a) + (beta * b) - gamma) / norm;
    }

    internal static bool IsUnit(double[]? n) => n is { Length: 3 } && Math.Abs(Math.Sqrt(Dot(n, n)) - 1) < 1e-3;

    internal static double RectDistance(PlaneRectMm r, double a, double b) =>
        Math.Sqrt(Math.Pow(Math.Max(Math.Max(r.AMin - a, 0), a - r.AMax), 2) + Math.Pow(Math.Max(Math.Max(r.BMin - b, 0), b - r.BMax), 2));

    /// <summary>The centroid of the facet's voting markers (<see cref="GeometryKernel.VotingCorners"/>: not the strays left out of its extent).</summary>
    internal static (double A, double B)? MarkerCentroid(WallGeometryDocument doc, string facetId)
    {
        var extent = doc.FindFacet(facetId)?.Facet.ExtentMm;
        var corners = GeometryKernel.VotingCorners(doc, facetId, extent);
        return corners.Count == 0 ? null : (corners.Average(c => c[0]), corners.Average(c => c[1]));
    }

    internal static double[] World(Wall3DFacet f, double a, double b) =>
    [
        f.Origin[0] + (a * f.U[0]) + (b * f.V[0]),
        f.Origin[1] + (a * f.U[1]) + (b * f.V[1]),
        f.Origin[2] + (a * f.U[2]) + (b * f.V[2]),
    ];

    internal static double Dot(double[] p, double[] q) => (p[0] * q[0]) + (p[1] * q[1]) + (p[2] * q[2]);

    private static Wall3DFacet WithOutline(
        Wall3DFacet facet, List<Wall3DFacet> facets, WallGeometryDocument doc, IReadOnlyDictionary<int, PlanTriangle> triangles)
    {
        if (!triangles.TryGetValue(facet.Segment, out var triangle))
        {
            return facet;
        }

        var centroid = MarkerCentroid(doc, facet.Id);
        var parent = facets
            .Where(f => f.Segment == triangle.ParentIndex && f.Id != facet.Id)
            .MinBy(f => Distance(Centre(f), Centre(facet)));
        var rightAngle = TriangleKeptSide.Mapped(facet, doc.World?.Up, triangle.RightAngle);
        var outline = (parent is null ? null : Clip(facet, parent, centroid, rightAngle))
                      ?? NearestSeamOutline(facet, facets, parent, centroid);
        return outline is null ? facet : WithShape(facet, outline);
    }

    private static IReadOnlyList<double[]>? NearestSeamOutline(
        Wall3DFacet facet, List<Wall3DFacet> facets, Wall3DFacet? parent, (double A, double B)? centroid) =>
        facets
            .Where(f => f.Id != facet.Id && f.Id != parent?.Id)
            .Select(f => (Other: f, Outline: Clip(facet, f, centroid)))
            .Where(c => c.Outline is not null)
            .Select(c => (c.Outline, Gap: SeamGapMm(facet, c.Other, c.Outline!)))
            .Where(c => c.Gap <= MaxSeamGapMm)
            .OrderBy(c => c.Gap)
            .Select(c => c.Outline)
            .FirstOrDefault();

    /// <summary>The solver's fold-clipped outline (<see cref="WallGeometryFacet.OutlineMm"/>, SfM models), counter-clockwise; null without one.</summary>
    private static IReadOnlyList<double[]>? SolvedOutline(WallGeometryDocument doc, string facetId)
    {
        if (doc.FindFacet(facetId)?.Facet.OutlineMm is not { Count: >= 3 } outline || outline.Any(p => p.Length < 2))
        {
            return null;
        }

        List<double[]> ring = [.. outline.Select(p => new[] { p[0], p[1] })];
        if (SignedArea(ring) < 0)
        {
            ring.Reverse();
        }

        return ring;
    }

    /// <summary>The plane's unit normal: <see cref="Wall3DFacet.Normal"/>, else U × V.</summary>
    private static double[] PlaneNormal(Wall3DFacet f)
    {
        if (IsUnit(f.Normal))
        {
            return f.Normal;
        }

        double[] n = [(f.U[1] * f.V[2]) - (f.U[2] * f.V[1]), (f.U[2] * f.V[0]) - (f.U[0] * f.V[2]), (f.U[0] * f.V[1]) - (f.U[1] * f.V[0])];
        var length = Math.Sqrt(Dot(n, n));
        return length < 1e-9 ? n : [n[0] / length, n[1] / length, n[2] / length];
    }

    private static double Area(IReadOnlyList<double[]> p) => Math.Abs(SignedArea(p));

    private static double SignedArea(IReadOnlyList<double[]> p) =>
        p.Select((q, i) => (q[0] * p[(i + 1) % p.Count][1]) - (p[(i + 1) % p.Count][0] * q[1])).Sum() / 2;

    private static void AddDistinct(List<double[]> points, double[] p)
    {
        if (points.Count == 0 || !Same(points[^1], p))
        {
            points.Add(p);
        }
    }

    private static bool Same(double[] p, double[] q) => Math.Abs(p[0] - q[0]) < MinEdgeMm && Math.Abs(p[1] - q[1]) < MinEdgeMm;

    private static double[] Centre(Wall3DFacet f) =>
        World(f, (f.Extent.AMin + f.Extent.AMax) / 2, (f.Extent.BMin + f.Extent.BMax) / 2);

    private static double Distance(double[] p, double[] q) =>
        Math.Sqrt(((p[0] - q[0]) * (p[0] - q[0])) + ((p[1] - q[1]) * (p[1] - q[1])) + ((p[2] - q[2]) * (p[2] - q[2])));
}
