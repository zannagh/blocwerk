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
}
