// <copyright file="LookOverRules.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.LookOver;

/// <summary>Which answers fit which finding, and the order the page works through them. Pure.</summary>
public static class LookOverRules
{
    /// <summary>A discarded detection the detector was at least this sure of may be a missed hold.</summary>
    public const double SureDetection = 0.9;

    /// <summary>A discarded detection that live holds cover by less than this overlaps no hold (outlines that only touch do not count).</summary>
    public const double NoOverlapBelow = 0.05;

    /// <summary>A discarded detection that live holds cover by at least this is probably a second detection of a hold that is there.</summary>
    public const double DuplicateOverlap = 0.5;

    /// <summary>
    /// A spot inside a hold's outline, or within this many pixels of the photo outside it, means the hold: "it is this
    /// hold", not "it is here". Only a hair's breadth: on a dense wall the spot of a hold is often right beside another.
    /// </summary>
    public const double SpotSnapPixels = 3;

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
        LookOverFindingKind.UnsureLink,
        LookOverFindingKind.DiscardedDetection,
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
    /// <returns>
    /// True for unsure matches, for old and new holds on one spot, for unsure links ("same hold") and for discarded
    /// detections ("not a hold"). Never
    /// for holds the update could not find: "still here" for all of them at once would put back whatever really is gone.
    /// </returns>
    public static bool CanSettleAll(LookOverFindingKind kind) =>
        kind is LookOverFindingKind.UnsureMatch or LookOverFindingKind.SameSpot or LookOverFindingKind.DiscardedDetection or LookOverFindingKind.UnsureLink;

    /// <summary>
    /// A discarded detection that is likely a hold the update missed: the detector was sure of it and no live hold is
    /// on its spot. Only these count as open findings and flag the wall.
    /// </summary>
    /// <param name="confidence">The detector's confidence.</param>
    /// <param name="overlap">How much of it live holds cover, 0..1.</param>
    /// <returns>True when it is.</returns>
    public static bool LikelyMissed(double? confidence, double? overlap) => confidence >= SureDetection && overlap < NoOverlapBelow;

    /// <summary>
    /// The hold a spot on a photo means: a hold the update found (not a kept copy) whose outline (or circle, without one)
    /// contains the spot or passes within <paramref name="marginPx"/> of it, the closest first; else none (the spot is
    /// bare wall: "it is here"). Measured in the photo's pixels: positions are normalised per axis and radii to the long
    /// side, so mixing them as they are would stretch every circle along the short side.
    /// </summary>
    /// <param name="holds">The live holds of the photo.</param>
    /// <param name="x">The spot across the photo, 0..1.</param>
    /// <param name="y">The spot down the photo, 0..1.</param>
    /// <param name="size">The photo's pixel size; null when it is not known, and then only a spot inside a hold counts.</param>
    /// <param name="marginPx">How close outside a hold still means it, in photo pixels.</param>
    /// <returns>The hold, or null.</returns>
    public static Services.PanelHold? HoldAtSpot(
        IEnumerable<Services.PanelHold> holds, double x, double y, (int Width, int Height)? size = null, double marginPx = SpotSnapPixels)
    {
        var known = size is { Width: > 0, Height: > 0 };
        var (width, height) = known ? ((double)size!.Value.Width, (double)size.Value.Height) : (1d, 1d);
        var margin = known ? marginPx : 0;
        return holds
            .Where(h => !h.Kept)
            .Select(h => (Hold: h, Gap: new HoldArea(h.X, h.Y, h.Radius, h.ShapePoints, known ? size : null).EdgeGap(x * width, y * height)))
            .Where(c => c.Gap <= margin)
            .OrderBy(c => c.Gap)
            .Select(c => c.Hold)
            .FirstOrDefault();
    }

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
        if (kind == LookOverFindingKind.DiscardedDetection)
        {
            return canEdit && !holdIsLive ? [LookOverAnswer.Settle, LookOverAnswer.AddHold] : [LookOverAnswer.Settle];
        }

        if (!holdIsLive || !canEdit)
        {
            return [LookOverAnswer.Settle];
        }

        if (kind == LookOverFindingKind.UnsureLink)
        {
            return [LookOverAnswer.Settle, LookOverAnswer.Unlink];
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
