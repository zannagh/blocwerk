// <copyright file="ReviewAnswer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>
/// An answer in the one review of a wall update. Card answers map onto <see cref="UpdateExceptionAnswer"/>, "moved?"
/// answers onto <see cref="RelocationDecision"/>; the rest are the review's own. Persisted on
/// <see cref="Entities.WallUpdateReviewAnswer"/>: append new values at the end.
/// </summary>
public enum ReviewAnswer
{
    /// <summary>Takes the person's answer back: the update's default applies again.</summary>
    Undo = 0,

    /// <summary>Card: keep (same hold, merge is right, old hold still there, add the detection).</summary>
    Keep = 1,

    /// <summary>Card: remove (old hold gone, don't merge, leave the detection out).</summary>
    Remove = 2,

    /// <summary>Card: not the same hold (the pair is taken apart).</summary>
    NotSame = 3,

    /// <summary>Not found: the old hold is still on the wall where it was.</summary>
    StillThere = 4,

    /// <summary>Not found: the old hold is gone (its boulders become history).</summary>
    Gone = 5,

    /// <summary>New hold: it is a hold, keep it.</summary>
    KeepNew = 6,

    /// <summary>New hold: it is no hold (noise, a duplicate, a bolt hole).</summary>
    NotAHold = 7,

    /// <summary>Left out: right, it is no hold.</summary>
    LeaveOut = 8,

    /// <summary>Left out: it is a hold the triage missed, add it.</summary>
    AddAsHold = 9,

    /// <summary>Moved?: the old hold physically moved to the new spot (its boulders are marked for review).</summary>
    Moved = 10,

    /// <summary>Moved?: the same hold, only the photo shifted.</summary>
    SameHold = 11,

    /// <summary>Moved?: not related.</summary>
    Dismiss = 12,
}
