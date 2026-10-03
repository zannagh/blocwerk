// <copyright file="RigidFit2D.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// A 2D rotation + translation (no scale, no mirror) mapping one point set onto another, fitted in the
/// least-squares sense — and robustly: every pair of correspondences proposes a fit, the one most points
/// agree with (within a tolerance) wins and is refitted on those points only. With a handful of markers
/// per surface that is exhaustive and cheap, and one misplaced marker cannot drag the others off.
/// </summary>
public sealed class RigidFit2D
{
    private readonly double cos;
    private readonly double sin;
    private readonly PlanVector shift;

    private RigidFit2D(double angle, PlanVector offset)
    {
        cos = Math.Cos(angle);
        sin = Math.Sin(angle);
        shift = offset;
    }

    /// <summary>Maps a point of the source set into the target set's frame.</summary>
    public PlanVector Apply(PlanVector p) => new PlanVector((cos * p.X) - (sin * p.Y), (sin * p.X) + (cos * p.Y)) + shift;

    /// <summary>Least-squares fit over all pairs; null for fewer than two.</summary>
    public static RigidFit2D? LeastSquares(IReadOnlyList<(PlanVector From, PlanVector To)> pairs)
    {
        if (pairs.Count < 2)
        {
            return null;
        }

        var cFrom = Centroid(pairs.Select(p => p.From));
        var cTo = Centroid(pairs.Select(p => p.To));
        double dot = 0, cross = 0;
        foreach (var (from, to) in pairs)
        {
            var a = from - cFrom;
            var b = to - cTo;
            dot += a.Dot(b);
            cross += (a.X * b.Y) - (a.Y * b.X);
        }

        var angle = Math.Atan2(cross, dot);
        var rotated = new RigidFit2D(angle, new PlanVector(0, 0)).Apply(cFrom);
        return new RigidFit2D(angle, cTo - rotated);
    }

    /// <summary>
    /// The fit most pairs agree with (residual below <paramref name="toleranceMm"/>), refitted on them.
    /// With two pairs it is simply their least-squares fit.
    /// </summary>
    public static RigidFit2D? Robust(IReadOnlyList<(PlanVector From, PlanVector To)> pairs, double toleranceMm)
    {
        if (pairs.Count <= 2)
        {
            return LeastSquares(pairs);
        }

        List<(PlanVector From, PlanVector To)>? bestInliers = null;
        var bestError = double.MaxValue;
        for (var i = 0; i < pairs.Count; i++)
        {
            for (var j = i + 1; j < pairs.Count; j++)
            {
                var candidate = LeastSquares([pairs[i], pairs[j]])!;
                var inliers = pairs.Where(p => candidate.Residual(p) < toleranceMm).ToList();
                var error = inliers.Sum(candidate.Residual);
                if (bestInliers is null || inliers.Count > bestInliers.Count || (inliers.Count == bestInliers.Count && error < bestError))
                {
                    (bestInliers, bestError) = (inliers, error);
                }
            }
        }

        return LeastSquares(bestInliers!.Count >= 2 ? bestInliers : pairs);
    }

    /// <summary>Distance between a mapped source point and its target.</summary>
    public double Residual((PlanVector From, PlanVector To) pair) => (Apply(pair.From) - pair.To).Length;

    private static PlanVector Centroid(IEnumerable<PlanVector> points)
    {
        var list = points.ToList();
        return new PlanVector(list.Average(p => p.X), list.Average(p => p.Y));
    }
}
