// <copyright file="HoldOutlineBoolean.Cut.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using SkiaSharp;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// The split: an outline cut in two along a drawn line or freehand stroke.
/// </summary>
/// <remarks>
/// The cut is the polygon-by-polyline kind, not a thin band subtracted: a stretch of the stroke that runs inside the
/// outline (from where it crosses the boundary in to where it crosses back out) becomes a shared edge of both parts, so
/// the parts tile the hold exactly and nothing is lost along the cut. Each part is the boundary walked from one crossing
/// to the other plus that stretch; holes are then cut out of both with <see cref="SKPath.Op(SKPath, SKPathOp)"/>, which
/// also resolves a stroke that loops over itself. When the stroke runs inside a concave hold more than once, the stretch
/// that leaves the most balanced pair is taken. The browser preview (<c>hold-split-line.js</c>) runs the same walk.
/// </remarks>
internal static partial class HoldOutlineBoolean
{
    /// <summary>The smallest share of the hold either part must keep.</summary>
    public const double MinPartShare = 0.04;

    /// <summary>The smallest part, as an equivalent radius in normalized units (about 8 px on a 4000 px photo).</summary>
    public const double MinPartRadius = 0.002;

    /// <summary>Cuts an outline in two along a stroke.</summary>
    /// <param name="shape">The hold's outline (normalized).</param>
    /// <param name="stroke">The stroke, normalized, in drawing order.</param>
    /// <param name="aspect">Photo width / height.</param>
    /// <returns>The two parts, larger first, or the reason the cut is refused.</returns>
    public static HoldOutlineCut Split(OutlineRings shape, IReadOnlyList<P2> stroke, double aspect)
    {
        if (stroke.Count < 2)
        {
            return HoldOutlineCut.Refused("Draw a line across the hold.");
        }

        var outer = Scaled(shape.Outer, aspect);
        if (ShapeGeometry.SignedArea(outer) < 0)
        {
            outer.Reverse();
        }

        var line = Scaled(stroke, aspect);
        var passages = Passages(outer, line);
        if (passages.Count == 0)
        {
            return HoldOutlineCut.Refused("The line has to cross the whole hold: start outside it and end outside it.");
        }

        // A stroke across a concave hold can run inside it more than once; the stretch that halves it best is the cut.
        var ordered = passages
            .Select(p => new[] { Part(Walk(outer, p.Exit, p.Entry, p.Inside), shape.Holes, aspect), Part(Walk(outer, p.Entry, p.Exit, Reversed(p.Inside)), shape.Holes, aspect) })
            .Where(parts => parts.All(part => part is not null))
            .Select(parts => parts.Select(part => part!).OrderByDescending(part => part.Area).ToList())
            .MaxBy(parts => parts[1].Area);
        if (ordered is null)
        {
            return HoldOutlineCut.Refused("That cut does not leave two pieces. Draw it once across the hold.");
        }

        double total = ordered.Sum(p => p.Area);
        var smaller = ordered[1];
        if (smaller.Area < MinPartShare * total || Math.Sqrt(smaller.Area / Math.PI) < MinPartRadius)
        {
            return HoldOutlineCut.Refused("One of the two pieces would be tiny. Cut closer to the middle of the hold.");
        }

        return new HoldOutlineCut(ordered[0], smaller, null);
    }

    /// <summary>One part: its outer ring (scaled space) minus the hold's holes, the largest piece if the holes cut it up.</summary>
    private static OutlineRings? Part(List<P2> ring, IReadOnlyList<IReadOnlyList<P2>> holes, double aspect)
    {
        using var path = new SKPath { FillType = SKPathFillType.Winding };
        path.MoveTo((float)ring[0].X, (float)ring[0].Y);
        foreach (var p in ring.Skip(1))
        {
            path.LineTo((float)p.X, (float)p.Y);
        }

        path.Close();
        using var cutOut = ToPath(new OutlineRings([], holes), aspect);
        using var result = path.Op(cutOut, SKPathOp.Difference);
        return result is null ? null : Pieces(result, aspect).FirstOrDefault();
    }

    /// <summary>
    /// Every stretch of the stroke that runs inside the outline: where it crosses in, the stroke's vertices in between,
    /// where it crosses out, and the edges both crossings sit on.
    /// </summary>
    private static List<((int Edge, double U, P2 Point) Entry, (int Edge, double U, P2 Point) Exit, List<P2> Inside)> Passages(List<P2> outer, List<P2> line)
    {
        var crossings = Crossings(outer, line);
        var passages = new List<((int Edge, double U, P2 Point) Entry, (int Edge, double U, P2 Point) Exit, List<P2> Inside)>();
        for (int k = 0; k + 1 < crossings.Count; k++)
        {
            var (inT, inEdge, inU, inP) = crossings[k];
            var (outT, outEdge, outU, outP) = crossings[k + 1];
            if (outT - inT < 1e-9 || !ShapeGeometry.Contains(outer, PointAt(line, (inT + outT) / 2)))
            {
                continue;
            }

            var inside = Enumerable.Range(0, line.Count).Where(i => i > inT && i < outT).Select(i => line[i]).ToList();
            passages.Add(((inEdge, inU, inP), (outEdge, outU, outP), inside));
        }

        return passages;
    }

    /// <summary>Every crossing of the stroke with the outline, ordered along the stroke (t = segment index + fraction).</summary>
    private static List<(double T, int Edge, double U, P2 Point)> Crossings(List<P2> outer, List<P2> line)
    {
        var found = new List<(double T, int Edge, double U, P2 Point)>();
        for (int i = 0; i + 1 < line.Count; i++)
        {
            for (int e = 0; e < outer.Count; e++)
            {
                var a = outer[e];
                var b = outer[(e + 1) % outer.Count];
                if (Intersect(line[i], line[i + 1], a, b) is { } hit)
                {
                    found.Add((i + hit.S, e, hit.U, line[i] + ((line[i + 1] - line[i]) * hit.S)));
                }
            }
        }

        return found.OrderBy(c => c.T).ToList();
    }

    private static (double S, double U)? Intersect(P2 p, P2 p2, P2 a, P2 b)
    {
        var r = p2 - p;
        var s = b - a;
        double denom = P2.Cross(r, s);
        if (Math.Abs(denom) < 1e-12)
        {
            return null;
        }

        double t = P2.Cross(a - p, s) / denom;
        double u = P2.Cross(a - p, r) / denom;
        return t is >= 0 and < 1 && u is >= 0 and < 1 ? (t, u) : null;
    }

    /// <summary>The boundary walked forward from one crossing to the other, then the cut back to the start.</summary>
    private static List<P2> Walk(List<P2> outer, (int Edge, double U, P2 Point) from, (int Edge, double U, P2 Point) to, List<P2> cut)
    {
        var ring = new List<P2> { from.Point };
        bool sameEdgeAhead = from.Edge == to.Edge && to.U > from.U;
        if (!sameEdgeAhead)
        {
            int e = from.Edge;
            do
            {
                e = (e + 1) % outer.Count;
                ring.Add(outer[e]);
            }
            while (e != to.Edge);
        }

        ring.Add(to.Point);
        ring.AddRange(cut);
        return ring;
    }

    private static List<P2> Reversed(List<P2> points) => Enumerable.Reverse(points).ToList();

    private static P2 PointAt(List<P2> line, double t)
    {
        int i = Math.Clamp((int)Math.Floor(t), 0, line.Count - 2);
        return line[i] + ((line[i + 1] - line[i]) * (t - i));
    }
}
