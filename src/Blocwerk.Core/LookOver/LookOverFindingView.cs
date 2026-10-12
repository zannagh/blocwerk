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
/// <param name="HasModel">There may be a 3D texture picture.</param>
/// <param name="PhotoScore">How well the new photo matches the old one at the spot (-1..1).</param>
/// <param name="Confidence">The matcher's confidence (unsure match).</param>
/// <param name="Converted">An unsure match a person called a different hold.</param>
/// <param name="Boulders">The current boulders that use the hold.</param>
/// <param name="Answers">The answers it takes now from this user (empty once it is not open).</param>
/// <param name="MergeNeedsAdmin">A new hold sits on the spot, but merging holds is for wall admins and the user is not one.</param>
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
    bool MergeNeedsAdmin = false);
