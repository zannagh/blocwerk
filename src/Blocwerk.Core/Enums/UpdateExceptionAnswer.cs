// <copyright file="UpdateExceptionAnswer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>A person's answer to a confirm-screen card (<see cref="Entities.WallUpdateException"/>).</summary>
public enum UpdateExceptionAnswer
{
    /// <summary>Keep the old hold, confirm the match, or keep the detection as a new hold.</summary>
    Keep = 0,

    /// <summary>Remove the old hold, or leave the detection out (not offered for a low-confidence match).</summary>
    Remove = 1,

    /// <summary>Back to the update's default, unanswered.</summary>
    Undo = 2,

    /// <summary>
    /// The detection is not the same hold (an unsure match or a hand-placed merge): the pair is taken apart as the full review's
    /// unlink does. The old hold is carried where the warp puts it, unconfirmed and marked for review, and the detection
    /// becomes a new hold. Only for <see cref="UpdateExceptionKind.LowConfidenceMatch"/> and
    /// <see cref="UpdateExceptionKind.MatchedToHandPlaced"/> (the merge is undone as with <see cref="Remove"/>, which keeps the old
    /// hold confirmed instead).
    /// </summary>
    NotSame = 3,
}
