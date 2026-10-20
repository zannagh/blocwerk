// <copyright file="PromotePayload.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// The one builder of what a promote is handed: the recorded decisions topped up with everything the matcher knows that the
/// promote acts on. The quick flow's move preview, its Apply and the step-by-step review all build it here, so the moves the
/// user is shown are the moves the promote derives (a missing warped outline once made every quick Apply on The Attic fail).
/// </summary>
public static class PromotePayload
{
    /// <summary>The decisions with the matcher's warp positions and outlines, hand-placed merges and holds not found again.</summary>
    /// <param name="decisions">The (scoped) decisions.</param>
    /// <param name="matched">The matched session.</param>
    /// <returns>The payload, without <see cref="BigUpdateConfirmation.ExpectedMovesVersion"/>.</returns>
    public static BigUpdateConfirmation For(BigUpdateConfirmation decisions, BigUpdateSession matched) =>
        decisions with
        {
            CarriedWarpPositions = matched.CarriedWarpPositions,
            CarriedWarpShapes = matched.CarriedWarpShapes,
            HandPlacedMergeOldIds = StandingMerges(decisions, matched),
            NotFoundAgain = matched.NotFoundAgain,
            ExpectedMovesVersion = null,
        };

    /// <summary>
    /// The matcher's hand-placed merges the decisions still make: the old hold's carry row points at the merged detection.
    /// A merge the user took apart (unlinked, paired with another detection, "not the same hold", removed) is no merge: that
    /// hold keeps its warped outline and is measured like any other carried pair.
    /// </summary>
    /// <param name="decisions">The decisions.</param>
    /// <param name="matched">The matched session.</param>
    /// <returns>The old hold ids.</returns>
    public static List<Guid> StandingMerges(BigUpdateConfirmation decisions, BigUpdateSession matched)
    {
        var twins = decisions.Carryover.Where(d => d.Kind != CarryKind.Removed && d.NewHoldId is not null).GroupBy(d => d.OldHoldId).ToDictionary(g => g.Key, g => g.Last().NewHoldId);
        return (matched.HandPlacedMerges ?? []).Where(m => twins.GetValueOrDefault(m.OldHoldId) == m.NewHoldId).Select(m => m.OldHoldId).ToList();
    }
}
