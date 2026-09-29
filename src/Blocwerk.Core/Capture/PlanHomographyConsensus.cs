// <copyright file="PlanHomographyConsensus.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Capture;

/// <summary>What one basis homography says about every marker of the segment.</summary>
internal sealed class PlanHomographyConsensus(PlaneHomography h, PlaneHomography inverse, List<PlanLayoutView> basis, double toleranceMm)
{
    private readonly Dictionary<PlanLayoutView, (double OffMm, double SizeRatio)> verdicts = [];

    public int Inliers { get; private set; }

    private double Cost { get; set; }

    public void Score(List<PlanLayoutView> members)
    {
        foreach (var m in members)
        {
            var (x, y) = inverse.Apply(m.X, m.Up);
            var off = double.IsFinite(x) ? Math.Sqrt(Math.Pow(x - m.PlanX, 2) + Math.Pow(y - m.PlanY, 2)) : double.PositiveInfinity;
            var (dxx, dxy, dyx, dyy) = h.Jacobian(m.PlanX, m.PlanY);
            var predicted = Math.Sqrt(Math.Abs((dxx * dyy) - (dxy * dyx))) * m.SizeMm;
            var ratio = predicted > 0 ? m.Marker.SidePx / predicted : double.PositiveInfinity;
            verdicts[m] = (off, ratio);
            Inliers += Fits(m) ? 1 : 0;
            Cost += Math.Min(off, toleranceMm);
        }
    }

    public bool Fits(PlanLayoutView m) =>
        verdicts[m] is var (off, ratio) && off <= toleranceMm && ratio >= 1 / CapturePlanLayoutCheck.MaxSizeRatio && ratio <= CapturePlanLayoutCheck.MaxSizeRatio;

    public bool IsBetterThan(PlanHomographyConsensus other) => Inliers > other.Inliers || (Inliers == other.Inliers && Cost < other.Cost);

    public string Describe(PlanLayoutView m)
    {
        var (off, ratio) = verdicts[m];
        var others = CapturePlanLayoutCheck.Ids(basis);
        return off > toleranceMm
            ? $"{(double.IsFinite(off) ? off.ToString("0", CultureInfo.InvariantCulture) + " mm" : "far")} from where {others} place it"
            : $"looks {ratio.ToString("0.0", CultureInfo.InvariantCulture)}× the size {others} predict for it";
    }
}
