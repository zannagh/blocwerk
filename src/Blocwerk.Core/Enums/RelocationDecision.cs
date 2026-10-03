// <copyright file="RelocationDecision.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>
/// A person's answer to one "Possibly moved" suggestion in the carryover review.
/// </summary>
public enum RelocationDecision
{
    /// <summary>
    /// The new hold IS the old hold, physically moved: recorded as a confirmed Changed carry onto the new
    /// hold, so its boulders are flagged for revision (moved == changed).
    /// </summary>
    Moved = 0,

    /// <summary>
    /// The new hold IS the old hold, in place — only the photo shifted and the positional matcher missed
    /// it: recorded as a confirmed Carried verdict onto the new hold, exactly like a normal match; nothing
    /// is flagged.
    /// </summary>
    SameHold = 1,

    /// <summary>
    /// Not the same hold (or undo an earlier answer): both holds keep today's outcome.
    /// </summary>
    Dismiss = 2,
}
