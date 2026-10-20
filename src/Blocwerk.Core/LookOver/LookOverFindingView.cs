// <copyright file="LookOverFindingView.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.LookOver;

/// <summary>One finding, as the look-over page and the API show it.</summary>
/// <param name="Id">The finding.</param>
/// <param name="Kind">Why it needs a look.</param>
/// <param name="Status">Open, settled or superseded.</param>
/// <param name="Answer">The answer that settled it.</param>
/// <param name="Generation">The wall generation of the update it came from.</param>
/// <param name="OldHoldId">The hold before the update.</param>
/// <param name="HoldId">
/// The live hold it is about. Null for a detection that was left out, and once the hold is no longer a live hold of the
/// wall (deleted, or its panel was photographed again): such a finding can only be acknowledged.
/// </param>
/// <param name="OtherHoldId">The new hold on the same spot, while it is a live hold.</param>
/// <param name="HoldName">The hold's name, else its colour.</param>
/// <param name="HasOld">There is an old photo of the hold.</param>
/// <param name="HasModel">
/// There may be a 3D texture picture: a spot on the model is known, and the picture was not found to be missing or
/// empty when it was last asked for (the page then does not ask again).
/// </param>
/// <param name="PhotoScore">How well the new photo matches the old one at the spot (-1..1).</param>
/// <param name="Confidence">The matcher's confidence (unsure match).</param>
/// <param name="Converted">An unsure match a person called a different hold.</param>
/// <param name="Boulders">The current boulders that use the hold.</param>
/// <param name="Answers">The answers it takes now from this user (empty once it is not open).</param>
/// <param name="MergeNeedsAdmin">A new hold sits on the spot, but merging holds is for wall admins and the user is not one.</param>
/// <param name="AnsweredBy">Who answered it.</param>
/// <param name="AnswerBoulders">How many boulders used the hold when it was answered.</param>
/// <param name="ClearedBoulders">How many boulders the answer took the review mark off.</param>
/// <param name="CanUndo">The answer changed nothing on the wall and can be taken back.</param>
/// <param name="HoldKept">The hold is kept, not confirmed: the update did not find it again and nobody has said it is there.</param>
/// <param name="KeptAtUpdate">
/// The update could not find the hold and kept a copy of it: the finding belongs to the look-over's "holds the update
/// could not find", whatever was answered since.
/// </param>
/// <param name="CanReassign">The user may say which hold of the new photo it is (a merge: wall admins only).</param>
/// <param name="Covered">The old hold lies under the new hold on its spot (and can be removed in bulk while no boulder uses it).</param>
/// <param name="ClosedReason">Why it was closed without an answer here (status 3): what was done in the hold editor.</param>
/// <param name="Pictures">The pictures to show, each with the holds to outline on it.</param>
/// <param name="PanelId">The panel photo the finding's spot is on.</param>
/// <param name="SpotX">The spot across that photo, 0..1.</param>
/// <param name="SpotY">The spot down that photo, 0..1.</param>
/// <param name="DiscardReason">Why the update discarded the detection (a <see cref="NewHoldDiscardReason"/> name), when that was recorded.</param>
/// <param name="Overlap">How much of a discarded detection the live holds of its photo cover, 0..1.</param>
/// <param name="NewHoldBoulders">
/// How many boulders a "use the new hold" answered per boulder moved onto the new hold (the others became historic);
/// null when every boulder moved.
/// </param>
public sealed record LookOverFindingView(
    Guid Id,
    LookOverFindingKind Kind,
    LookOverFindingStatus Status,
    LookOverAnswer? Answer,
    int Generation,
    Guid? OldHoldId,
    Guid? HoldId,
    Guid? OtherHoldId,
    string? HoldName,
    bool HasOld,
    bool HasModel,
    double? PhotoScore,
    double? Confidence,
    bool Converted,
    IReadOnlyList<LookOverBoulder> Boulders,
    IReadOnlyList<LookOverAnswer> Answers,
    bool MergeNeedsAdmin = false,
    string? AnsweredBy = null,
    int? AnswerBoulders = null,
    int ClearedBoulders = 0,
    bool CanUndo = false,
    bool HoldKept = false,
    bool KeptAtUpdate = false,
    bool CanReassign = false,
    bool Covered = false,
    LookOverCloseReason? ClosedReason = null,
    IReadOnlyList<LookOverPicture>? Pictures = null,
    Guid? PanelId = null,
    double SpotX = 0,
    double SpotY = 0,
    string? DiscardReason = null,
    double Overlap = 0,
    int? NewHoldBoulders = null)
{
    /// <summary>Gets a value indicating whether this is a detection the update discarded.</summary>
    public bool Discarded => Kind == LookOverFindingKind.DiscardedDetection;

    /// <summary>Gets a value indicating whether a live hold covers most of the discarded detection: probably a second detection of that hold.</summary>
    public bool ProbablyDuplicate => Discarded && Overlap >= LookOverRules.DuplicateOverlap;

    /// <summary>Gets a value indicating whether it counts as an open finding: everything but a discarded detection that is not likely a missed hold.</summary>
    public bool CountsAsOpen => !Discarded || LookOverRules.LikelyMissed(Confidence, Overlap);

    /// <summary>Gets where a discarded detection stands among its kind: what overlaps no hold first, then the surest; 0 for every other finding.</summary>
    public double DiscardedOrder => Discarded ? (Overlap < LookOverRules.NoOverlapBelow ? 0 : ProbablyDuplicate ? 4 : 2) - (Confidence ?? 0) : 0;

    /// <summary>
    /// Gets a value indicating whether this is a hold the update could not find and nobody has confirmed: it is asked
    /// about in the look-over's own section ("is it still on the wall, and where?"), not as a card.
    /// </summary>
    public bool Missing => Status == LookOverFindingStatus.Open && HoldKept;
}
