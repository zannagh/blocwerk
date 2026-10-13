// <copyright file="LookOverAnswer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>What a person can say about a <see cref="Entities.WallLookOverFinding"/>. Which ones fit depends on its kind.</summary>
public enum LookOverAnswer
{
    /// <summary>
    /// It is fine as it is: "Same hold", "Keep old hold here", "Still there", "Keep hold", "Merge is right", "Understood".
    /// Nothing on the wall changes; the hold's review mark is cleared when the update set it for this finding.
    /// </summary>
    Settle = 0,

    /// <summary>
    /// An unsure match that is in fact another hold: the hold is marked as changed (its boulders are marked for review)
    /// and the finding stays open as <see cref="LookOverFindingKind.ReplacedByOther"/>.
    /// </summary>
    DifferentHold = 1,

    /// <summary>The old hold is gone: it is deleted from the wall, and the boulders that use it become historic.</summary>
    RemoveHold = 2,

    /// <summary>The boulders should use the new hold on the spot: the old hold is merged into it (can be undone).</summary>
    UseNewHold = 3,

    /// <summary>
    /// "It is this hold": a hold the update could not find IS a hold that is on the new photo. The kept copy is merged
    /// into that hold (its boulders move over, its name and other attributes fill what the hold lacks); can be undone
    /// like any duplicate merge.
    /// </summary>
    Reassign = 4,

    /// <summary>"It is here": the kept hold is moved to where it really is, which also confirms it.</summary>
    Moved = 5,

    /// <summary>"Add as a hold": a discarded detection becomes a live hold with its detected outline; can be undone while no boulder uses it.</summary>
    AddHold = 6,
}
