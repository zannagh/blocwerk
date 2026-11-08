// <copyright file="IHoldCarryMatcher.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// A matcher that can also run the carryover pass (an old photo's holds against the same panel's new photo) with the
/// carryover's stricter pick: an old hold is only paired with a detection that is unambiguously the nearest, so it never
/// "jumps" to a neighbouring detection and reports a move. The overlap pass between two panels keeps the plain
/// <see cref="IHoldOverlapMatcher.Match"/>, whose low-confidence links only ever reach the review as unsure.
/// </summary>
public interface IHoldCarryMatcher
{
    /// <summary>Like <see cref="IHoldOverlapMatcher.Match"/>, with the unique-pick rule of the carryover.</summary>
    /// <param name="leftImage">The old photo.</param>
    /// <param name="leftHolds">The old holds.</param>
    /// <param name="rightImage">The new photo.</param>
    /// <param name="rightHolds">The new detections.</param>
    /// <param name="direction">Advisory overlap direction.</param>
    /// <param name="diag">Optional diagnostics sink.</param>
    /// <param name="seed">Optional seed.</param>
    /// <returns>The proposals.</returns>
    HoldOverlapResult MatchCarry(
        byte[] leftImage,
        IReadOnlyList<MatcherHold> leftHolds,
        byte[] rightImage,
        IReadOnlyList<MatcherHold> rightHolds,
        HoldOverlapDirection direction,
        ILogger? diag = null,
        HoldOverlapSeed? seed = null);
}
