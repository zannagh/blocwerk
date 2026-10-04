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
}
