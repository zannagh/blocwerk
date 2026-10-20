// <copyright file="WallEnhancementDecision.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Entities;

/// <summary>The review answer on a <see cref="WallEnhancementItem"/>.</summary>
public enum WallEnhancementDecision
{
    /// <summary>Not answered yet.</summary>
    Pending = 0,

    /// <summary>Kept (written changes) or carried out (suggestions).</summary>
    Accepted = 1,

    /// <summary>Undone (written changes) or turned down (suggestions).</summary>
    Rejected = 2,

    /// <summary>Could not be acted on any more (the hold changed or went meanwhile); see the note.</summary>
    Skipped = 3,
}
