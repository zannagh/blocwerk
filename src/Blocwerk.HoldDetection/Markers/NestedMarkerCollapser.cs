// <copyright file="NestedMarkerCollapser.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.HoldDetection.Markers;

/// <summary>
/// Collapses same-id candidates that are one marker seen more than once. With a thin white border on a
/// darker wall the paper's outline decodes too (its outer ring is mostly the black border), and the
/// same square is found again at several threshold windows. Candidates are nested when each one's
/// centre lies inside the other; of such a cluster the innermost layer is the black square, and of that
/// layer the largest quad is kept (what OpenCV's own too-close filter keeps). Same-id candidates in
/// different places are left alone for the validator's duplicate-id rejection.
/// </summary>
internal static class NestedMarkerCollapser
{
    /// <summary>Threshold-window twins of one square differ by less than this in area.</summary>
    private const double LayerAreaRatio = 0.9;

    /// <summary>A quad this much smaller than another is not the same marker.</summary>
    private const double MinAreaRatio = 0.35;

    public static IReadOnlyList<MarkerCandidate> Collapse(IReadOnlyList<MarkerCandidate> candidates)
    {
        var areas = candidates.Select(c => Area(c.CornersPx)).ToArray();
        var cluster = Enumerable.Range(0, candidates.Count).ToArray();
        for (var i = 0; i < candidates.Count; i++)
        {
            for (var j = i + 1; j < candidates.Count; j++)
            {
                if (candidates[i].Id == candidates[j].Id && AreNested(candidates[i], areas[i], candidates[j], areas[j]))
                {
                    Union(cluster, i, j);
                }
            }
        }

        var kept = new List<MarkerCandidate>();
        foreach (var members in Enumerable.Range(0, candidates.Count).GroupBy(i => Find(cluster, i)))
        {
            var smallest = members.Min(i => areas[i]);
            var keep = members.Where(i => areas[i] * LayerAreaRatio <= smallest).MaxBy(i => areas[i]);
            kept.Add(candidates[keep]);
        }

        return kept;
    }

    private static bool AreNested(MarkerCandidate a, double areaA, MarkerCandidate b, double areaB) =>
        Math.Min(areaA, areaB) >= MinAreaRatio * Math.Max(areaA, areaB)
        && Contains(a.CornersPx, Centre(b.CornersPx))
        && Contains(b.CornersPx, Centre(a.CornersPx));

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            i = parent[i] = parent[parent[i]];
        }

        return i;
    }

    private static void Union(int[] parent, int a, int b) => parent[Find(parent, a)] = Find(parent, b);

    private static MarkerPoint Centre(IReadOnlyList<MarkerPoint> q) => new(q.Average(p => p.X), q.Average(p => p.Y));

    /// <summary>Point in polygon by the crossing rule.</summary>
    private static bool Contains(IReadOnlyList<MarkerPoint> q, MarkerPoint p)
    {
        var inside = false;
        for (int i = 0, j = q.Count - 1; i < q.Count; j = i++)
        {
            if ((q[i].Y > p.Y) != (q[j].Y > p.Y)
                && p.X < ((q[j].X - q[i].X) * (p.Y - q[i].Y) / (q[j].Y - q[i].Y)) + q[i].X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double Area(IReadOnlyList<MarkerPoint> q)
    {
        var sum = 0.0;
        for (var i = 0; i < q.Count; i++)
        {
            var a = q[i];
            var b = q[(i + 1) % q.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return Math.Abs(sum) / 2;
    }
}
