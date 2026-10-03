// <copyright file="CapturePlanLayoutCheck.Homography.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Capture;

/// <summary>The per-segment homography consensus (plan centre → image centre).</summary>
public static partial class CapturePlanLayoutCheck
{
    /// <summary>A segment needs this many markers in the photo for the homography to outvote one of them.</summary>
    internal const int MinHomographyMarkers = 5;

    /// <summary>A marker this far (mm, in the plan) from where the consensus places it does not fit ...</summary>
    internal const double HomographyToleranceMm = 400;

    /// <summary>... or this share of the consensus markers' plan span, whichever is larger.</summary>
    internal const double HomographyToleranceShare = 0.35;

    /// <summary>Apparent size over the size the consensus predicts at the planned spot: allowed band (1/x … x).</summary>
    internal const double MaxSizeRatio = 1.8;

    /// <summary>At most this many markers of a segment take part (the 4-subsets grow quickly).</summary>
    private const int MaxHomographyMarkers = 12;

    private static List<IgnoredCaptureMarker> CheckHomography(List<PlanLayoutView> members)
    {
        if (members.Count < MinHomographyMarkers)
        {
            return [];
        }

        var pool = members.OrderByDescending(m => m.MaxEdgePx).ThenBy(m => m.Marker.Id).Take(MaxHomographyMarkers).ToList();
        PlanHomographyConsensus? best = null;
        foreach (var subset in Subsets(pool.Count, 4))
        {
            var basis = subset.Select(i => pool[i]).ToList();
            if (Hypothesis(basis, members) is { } candidate && (best is null || candidate.IsBetterThan(best)))
            {
                best = candidate;
            }
        }

        List<PlanLayoutView> outliers = best is null || best.Inliers < 4 ? [] : members.Where(m => !best.Fits(m)).ToList();
        if (outliers.Count == 0 || members.Count - outliers.Count < 4)
        {
            return [];
        }

        members.RemoveAll(outliers.Contains);
        return outliers.Select(m => new IgnoredCaptureMarker(m.Marker, best!.Describe(m))).ToList();
    }

    /// <summary>The consensus of one 4-marker basis, or null when its homography is degenerate or seen from behind.</summary>
    private static PlanHomographyConsensus? Hypothesis(List<PlanLayoutView> basis, List<PlanLayoutView> members)
    {
        var h = PlaneHomography.Fit(basis.Select(b => new PointCorrespondence(b.PlanX, b.PlanY, b.X, b.Up)).ToList());
        var inverse = h?.Inverse();
        if (h is null || inverse is null || basis.Any(b => !FrontFacing(h, b)))
        {
            return null;
        }

        var span = basis.SelectMany(a => basis.Select(a.PlanDistance)).Max();
        var consensus = new PlanHomographyConsensus(h, inverse, basis, Math.Max(HomographyToleranceMm, HomographyToleranceShare * span));
        consensus.Score(members);
        return consensus;
    }

    /// <summary>Plan (x right, y up) → image (x right, y up): same handedness, positive scale, for a front view.</summary>
    private static bool FrontFacing(PlaneHomography h, PlanLayoutView at)
    {
        var (dxx, dxy, dyx, dyy) = h.Jacobian(at.PlanX, at.PlanY);
        var det = (dxx * dyy) - (dxy * dyx);
        return double.IsFinite(det) && det > 0;
    }

    private static IEnumerable<int[]> Subsets(int n, int k)
    {
        var idx = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return (int[])idx.Clone();
            var i = k - 1;
            while (i >= 0 && idx[i] == n - k + i)
            {
                i--;
            }

            if (i < 0)
            {
                yield break;
            }

            idx[i]++;
            for (var j = i + 1; j < k; j++)
            {
                idx[j] = idx[j - 1] + 1;
            }
        }
    }
}
