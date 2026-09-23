// <copyright file="GeometryAttachments.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Stitches solved facets into a net: starting from the first facet (the root), repeatedly attaches
/// the not-yet-attached facet whose edge lies closest in 3D to an edge of an attached one (skipping
/// pairings whose outline would cover an attached facet in the net), preferring
/// parallel edges (within 25°). The offset is the 3D position of the child's anchoring endpoint along
/// the parent edge, so the net keeps the measured slide between neighbours.
/// </summary>
internal static class GeometryAttachments
{
    private const double ParallelCos = 0.906; // cos 25°
    private const double NotParallelPenaltyMm = 10_000;
    private const double UncoveredWeight = 0.25;

    private static readonly PhotoSetup Placeholder = MarkerCameraPresets.Create(MarkerCameraPresets.Phone1X, 2500);

    public static PlanAttachment?[] Solve(IReadOnlyList<GeometryFacet> facets)
    {
        var result = new PlanAttachment?[facets.Count];
        var attached = new List<int>();
        if (facets.Count == 0)
        {
            return result;
        }

        attached.Add(0);
        while (attached.Count < facets.Count)
        {
            var candidates = new List<(double Score, int Child, PlanAttachment Attachment)>();
            for (var c = 0; c < facets.Count; c++)
            {
                if (!attached.Contains(c))
                {
                    candidates.AddRange(attached.SelectMany(p => Candidates(facets[p], facets[c])).Select(x => (x.Score, c, x.Attachment)));
                }
            }

            // The best fit whose outline stays clear of the facets already laid out; else the best fit.
            var ordered = candidates.OrderBy(x => x.Score).ToList();
            (double Score, int Child, PlanAttachment Attachment)? pick = null;
            foreach (var candidate in ordered)
            {
                if (!Overlaps(facets, result, attached, candidate.Child, candidate.Attachment))
                {
                    pick = candidate;
                    break;
                }
            }

            pick ??= ordered.Count > 0 ? ordered[0] : null;
            var child = pick?.Child ?? Enumerable.Range(0, facets.Count).First(i => !attached.Contains(i));

            // Without 3D frames to compare, line the facet up to the right of the previous one.
            result[child] = pick?.Attachment ?? new PlanAttachment(facets[attached[^1]].Index, SegmentEdge.Right, SegmentEdge.Left, 0);
            attached.Add(child);
        }

        return result;
    }

    /// <summary>Every (parent edge, child edge) pairing with its 3D fit score (lower is better).</summary>
    private static IEnumerable<(double Score, PlanAttachment Attachment)> Candidates(GeometryFacet parent, GeometryFacet child)
    {
        if (!HasFrame(parent) || !HasFrame(child))
        {
            yield break;
        }

        foreach (var pe in SegmentOutline.Edges(AsSegment(parent, null)))
        {
            foreach (var ce in SegmentOutline.Edges(AsSegment(child, null)))
            {
                var p0 = World(parent, pe.Start);
                var p1 = World(parent, pe.End);
                var anchor = World(child, pe.StartsAtFrom ? ce.To : ce.From);
                var other = World(child, pe.StartsAtFrom ? ce.From : ce.To);
                var along = Unit(Sub(p1, p0));
                var offset = Math.Round(Dot(Sub(anchor, p0), along), 1);
                yield return (EdgeDistance(p0, p1, anchor, other), new PlanAttachment(parent.Index, pe.Edge, ce.Edge, offset));
            }
        }
    }

    /// <summary>True when attaching the child this way makes its outline cover an attached facet's.</summary>
    private static bool Overlaps(IReadOnlyList<GeometryFacet> facets, PlanAttachment?[] result, List<int> attached, int child, PlanAttachment attachment)
    {
        var segments = attached.Select(i => AsSegment(facets[i], result[i])).Append(AsSegment(facets[child], attachment)).ToList();
        var plan = new MarkerPlan(MarkerPlan.CurrentSchemaVersion, ArucoDict4X4.DictionaryName, Placeholder, segments, []);
        return NetLayout.Compute(plan).Issues.Any(i => i.Code == "net-overlap");
    }

    /// <summary>
    /// Mean distance of the child edge's ends to the parent edge's line, plus a share of the child edge
    /// that runs past the parent edge (so a long kickboard hangs off the long wall above it, not off a
    /// narrow neighbour that happens to be collinear).
    /// </summary>
    private static double EdgeDistance(double[] p0, double[] p1, double[] c0, double[] c1)
    {
        var length = Norm(Sub(p1, p0));
        var along = Unit(Sub(p1, p0));
        double LineDistance(double[] q)
        {
            var d = Sub(q, p0);
            var t = Dot(d, along);
            return Norm(Sub(d, Scale(along, t)));
        }

        var t0 = Dot(Sub(c0, p0), along);
        var t1 = Dot(Sub(c1, p0), along);
        var lo = Math.Min(t0, t1);
        var hi = Math.Max(t0, t1);
        var covered = Math.Max(0, Math.Min(hi, length) - Math.Max(lo, 0));
        var gap = Math.Max(0, Math.Max(lo - length, -hi));
        var uncovered = (hi - lo - covered) * UncoveredWeight;
        var parallel = Math.Abs(Dot(Unit(Sub(c1, c0)), along)) >= ParallelCos;
        return ((LineDistance(c0) + LineDistance(c1)) / 2) + gap + uncovered + (parallel ? 0 : NotParallelPenaltyMm);
    }

    private static PlanSegment AsSegment(GeometryFacet f, PlanAttachment? attachment) => new(
        f.Index, f.Name, SegmentShape.Rectangle, f.Extent.Width, f.Extent.Height, TriangleCorner.BottomLeft, 0, 0, attachment);

    private static bool HasFrame(GeometryFacet f) => f.Origin is { Length: 3 } && f.U is { Length: 3 } && f.V is { Length: 3 };

    private static double[] World(GeometryFacet f, PlanVector local)
    {
        var a = local.X + f.Extent.AMin;
        var b = local.Y + f.Extent.BMin;
        return [f.Origin![0] + (f.U![0] * a) + (f.V![0] * b), f.Origin[1] + (f.U[1] * a) + (f.V[1] * b), f.Origin[2] + (f.U[2] * a) + (f.V[2] * b)];
    }

    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double[] Scale(double[] a, double k) => [a[0] * k, a[1] * k, a[2] * k];

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double Norm(double[] a) => Math.Sqrt(Dot(a, a));

    private static double[] Unit(double[] a)
    {
        var n = Norm(a);
        return n < 1e-12 ? [0, 0, 0] : Scale(a, 1 / n);
    }
}
