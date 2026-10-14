// <copyright file="HoldStackingOptions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.HoldStacks;

/// <summary>
/// The thresholds of <see cref="HoldStacking"/>: when a hold that lies inside another hold's outline counts as sitting
/// ON it. <see cref="Default"/> is what every automatic path uses; a caller that judges other material (raw detector
/// output, say) passes its own.
/// </summary>
/// <remarks>
/// The defaults were set on the real wall (the Attic, 1111 live holds, 2026-10-13). With 1.8 times the area and plain
/// circles as bases, 15 holds were stacked there and 9 of them wrongly: a second record of the same hold (area ratios 2
/// to 3.5), or a neighbour that a coarse circle happens to cover. A hold that really sits on another one is far smaller
/// than it; from 4 times the area on, with an outlined base, none of the wrong ones is left.
/// </remarks>
/// <param name="MinInside">The share of the upper hold's area that must lie inside the base's outline, 0..1.</param>
/// <param name="MinAreaRatio">How many times larger (by area) the base must be.</param>
/// <param name="OutlinedBaseOnly">
/// Only a hold with a traced or drawn outline can be a base. A plain circle (placed by hand, or a detection that could
/// not be outlined) marks roughly where a hold is: what lies in it is usually that hold again, or its neighbour.
/// </param>
public sealed record HoldStackingOptions(double MinInside = 0.7, double MinAreaRatio = 4, bool OutlinedBaseOnly = true)
{
    /// <summary>The thresholds every automatic path uses.</summary>
    public static HoldStackingOptions Default { get; } = new();
}
