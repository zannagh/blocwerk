// <copyright file="LookOverRules.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.LookOver;

/// <summary>Which answers fit which finding, and the order the page works through them. Pure.</summary>
public static class LookOverRules
{
    /// <summary>The kinds in the order they are shown: what touches boulders first, the noisy and the harmless last.</summary>
    public static readonly IReadOnlyList<LookOverFindingKind> Order =
    [
        LookOverFindingKind.ReplacedByOther,
        LookOverFindingKind.NotFound,
        LookOverFindingKind.PossiblyRemoved,
        LookOverFindingKind.UnsureMatch,
        LookOverFindingKind.SameSpot,
        LookOverFindingKind.MatchedToHandPlaced,
        LookOverFindingKind.HandPlacedAmbiguous,
        LookOverFindingKind.ConflictingNew,
    ];

    /// <summary>The kinds whose old hold was kept in place, so a new hold can sit on the same spot.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for those.</returns>
    public static bool CanHaveOther(LookOverFindingKind kind) =>
        kind is LookOverFindingKind.ReplacedByOther or LookOverFindingKind.NotFound or LookOverFindingKind.PossiblyRemoved or LookOverFindingKind.SameSpot;

    /// <summary>The answers an open finding takes right now.</summary>
    /// <param name="kind">Its kind.</param>
    /// <param name="holdExists">Its live hold still exists.</param>
    /// <param name="canMerge">A new hold sits on the spot, on the same photo, and the user may merge holds (wall admin).</param>
    /// <returns>The answers; settling is always one of them.</returns>
    public static IReadOnlyList<LookOverAnswer> AnswersFor(LookOverFindingKind kind, bool holdExists, bool canMerge)
    {
        if (!holdExists)
        {
            return [LookOverAnswer.Settle];
        }

        if (kind == LookOverFindingKind.UnsureMatch)
        {
            return [LookOverAnswer.Settle, LookOverAnswer.DifferentHold];
        }

        if (!CanHaveOther(kind))
        {
            return [LookOverAnswer.Settle];
        }

        return canMerge
            ? [LookOverAnswer.Settle, LookOverAnswer.UseNewHold, LookOverAnswer.RemoveHold]
            : [LookOverAnswer.Settle, LookOverAnswer.RemoveHold];
    }
}
