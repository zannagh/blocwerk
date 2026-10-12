// <copyright file="HoldColorEstimate.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Holds.Colors;

/// <summary>The colour estimate of one hold on one photo.</summary>
/// <param name="Color">The proposed <see cref="HoldPalette"/> key, or null when the estimate is not sure enough.</param>
/// <param name="Verdict">Why.</param>
/// <param name="Area">The hold's sampled area in photo pixels: the larger view of a linked hold is the better one.</param>
public sealed record HoldColorEstimate(string? Color, HoldColorVerdict Verdict, double Area = 0)
{
    /// <summary>An estimate that proposes nothing.</summary>
    /// <param name="verdict">Why not.</param>
    /// <returns>The estimate.</returns>
    public static HoldColorEstimate Unset(HoldColorVerdict verdict) => new(null, verdict);
}
