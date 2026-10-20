// <copyright file="HoldOutlineCut.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>The outcome of <see cref="HoldOutlineBoolean.Split"/>: two parts, or why the cut was refused.</summary>
/// <param name="Larger">The part with the larger area (keeps the hold's identity).</param>
/// <param name="Smaller">The other part (becomes a new hold).</param>
/// <param name="Refusal">Why the cut was refused, for the person who drew it; null when it worked.</param>
internal sealed record HoldOutlineCut(OutlineRings? Larger, OutlineRings? Smaller, string? Refusal)
{
    /// <summary>A refused cut.</summary>
    /// <param name="reason">Why, in words for the person who drew it.</param>
    /// <returns>The outcome.</returns>
    public static HoldOutlineCut Refused(string reason) => new(null, null, reason);
}
