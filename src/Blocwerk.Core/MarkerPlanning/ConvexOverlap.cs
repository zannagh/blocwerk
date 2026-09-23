// <copyright file="ConvexOverlap.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>Separating-axis test for two convex polygons (segment outlines are all convex).</summary>
internal static class ConvexOverlap
{
    /// <summary>
    /// True when the interiors overlap by more than <paramref name="toleranceMm"/> along every
    /// separating axis — so outlines that merely share an edge or a corner do not count.
    /// </summary>
    public static bool Overlaps(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b, double toleranceMm)
    {
        return !HasSeparatingAxis(a, a, b, toleranceMm) && !HasSeparatingAxis(b, a, b, toleranceMm);
    }

    private static bool HasSeparatingAxis(IReadOnlyList<double[]> edgesOf, IReadOnlyList<double[]> a, IReadOnlyList<double[]> b, double tolerance)
    {
        for (var i = 0; i < edgesOf.Count; i++)
        {
            var p = edgesOf[i];
            var q = edgesOf[(i + 1) % edgesOf.Count];
            var axis = new PlanVector(-(q[1] - p[1]), q[0] - p[0]).Normalized();
            if (axis == default)
            {
                continue;
            }

            var (minA, maxA) = Project(a, axis);
            var (minB, maxB) = Project(b, axis);
            if (Math.Min(maxA, maxB) - Math.Max(minA, minB) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static (double Min, double Max) Project(IReadOnlyList<double[]> polygon, PlanVector axis)
    {
        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var p in polygon)
        {
            var d = (p[0] * axis.X) + (p[1] * axis.Y);
            min = Math.Min(min, d);
            max = Math.Max(max, d);
        }

        return (min, max);
    }
}
