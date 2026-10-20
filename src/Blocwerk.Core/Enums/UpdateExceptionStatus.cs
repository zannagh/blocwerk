// <copyright file="UpdateExceptionStatus.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>Where a confirm-screen card (<see cref="Entities.WallUpdateException"/>) stands.</summary>
public enum UpdateExceptionStatus
{
    /// <summary>Nobody decided: the update's default stands, and an old hold is marked for review when it goes live.</summary>
    Pending = 0,

    /// <summary>
    /// Kept: the old hold stays (possibly removed), the match is right (low confidence), or the detection becomes a new
    /// hold (conflicting evidence).
    /// </summary>
    Kept = 1,

    /// <summary>Removed: the old hold is gone (possibly removed), or the detection is left out (conflicting evidence).</summary>
    Removed = 2,

    /// <summary>
    /// Not the same hold (an unsure match or a hand-placed merge, <see cref="UpdateExceptionAnswer.NotSame"/>): the pair is taken
    /// apart, the old hold goes live carried where it was predicted, unconfirmed and marked for review.
    /// </summary>
    NotSame = 3,
}
