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

    /// <summary>Where a kind stands in <see cref="Order"/> (lower is more consequential).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>Its rank; an unknown kind comes last.</returns>
    public static int Rank(LookOverFindingKind kind)
    {
        for (var i = 0; i < Order.Count; i++)
        {
            if (Order[i] == kind)
            {
                return i;
            }
        }

        return Order.Count;
    }

    /// <summary>The kinds whose old hold was kept in place, so a new hold can sit on the same spot.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for those.</returns>
    public static bool CanHaveOther(LookOverFindingKind kind) =>
        kind is LookOverFindingKind.ReplacedByOther or LookOverFindingKind.NotFound or LookOverFindingKind.PossiblyRemoved or LookOverFindingKind.SameSpot;

    /// <summary>
    /// Settling this kind says the update's reason was wrong (the hold is the same one, the hold is still there), so
    /// boulders that were marked only for it may be cleared. Keeping a replaced or a possibly removed hold does not
    /// say that: the hold's situation did change.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for an unsure match and a hold not found again.</returns>
    public static bool SettlingWithdrawsTheReason(LookOverFindingKind kind) =>
        kind is LookOverFindingKind.UnsureMatch or LookOverFindingKind.NotFound;

    /// <summary>The kinds that can be settled all at once.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for unsure matches and for old and new holds on one spot.</returns>
    public static bool CanSettleAll(LookOverFindingKind kind) =>
        kind is LookOverFindingKind.UnsureMatch or LookOverFindingKind.SameSpot;

    /// <summary>The answers that change a hold or its boulders, so they ask first and say which boulders they saw.</summary>
    /// <param name="answer">The answer.</param>
    /// <returns>True for everything but settling.</returns>
    public static bool ChangesTheWall(LookOverAnswer answer) => answer != LookOverAnswer.Settle;

    /// <summary>The answers an open finding takes right now from one user.</summary>
    /// <param name="kind">Its kind.</param>
    /// <param name="holdIsLive">Its hold is still a live hold of the wall.</param>
    /// <param name="canEdit">The user may edit holds on the wall (owner, admin, moderator): what marking and deleting a hold check.</param>
    /// <param name="canMerge">A new hold sits on the spot, on the same photo, and the user may merge holds (owner, admin).</param>
    /// <returns>The answers; settling is always one of them.</returns>
    public static IReadOnlyList<LookOverAnswer> AnswersFor(LookOverFindingKind kind, bool holdIsLive, bool canEdit, bool canMerge)
    {
        if (!holdIsLive || !canEdit)
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
