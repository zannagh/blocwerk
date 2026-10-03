// <copyright file="KnownHoldReference.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Proposals;

/// <summary>
/// Where an existing hold can be in 3D, as a segment: its panel photo's ray from the flat facet position up
/// to <see cref="HoldProposalFinder.KnownRayMm"/> off the wall (the hold is somewhere on it, depending on how
/// far it stands out), or just its point on a volume / on the facet when the panel camera is unknown.
/// </summary>
/// <param name="Id">The hold.</param>
/// <param name="Start">One end, world mm.</param>
/// <param name="End">The other end (equal to <paramref name="Start"/> for a point).</param>
/// <param name="ToleranceMm">How near a cluster must come to be this hold, mm.</param>
public sealed record KnownHoldReference(Guid Id, double[] Start, double[] End, double ToleranceMm)
{
    /// <summary>Distance from <paramref name="p"/> to the segment, mm.</summary>
    /// <param name="p">World point.</param>
    /// <returns>The distance.</returns>
    public double DistanceTo(double[] p)
    {
        double[] d = [End[0] - Start[0], End[1] - Start[1], End[2] - Start[2]];
        double[] v = [p[0] - Start[0], p[1] - Start[1], p[2] - Start[2]];
        var len2 = (d[0] * d[0]) + (d[1] * d[1]) + (d[2] * d[2]);
        var t = len2 <= 0 ? 0 : Math.Clamp(((v[0] * d[0]) + (v[1] * d[1]) + (v[2] * d[2])) / len2, 0, 1);
        double x = v[0] - (t * d[0]), y = v[1] - (t * d[1]), z = v[2] - (t * d[2]);
        return Math.Sqrt((x * x) + (y * y) + (z * z));
    }
}
