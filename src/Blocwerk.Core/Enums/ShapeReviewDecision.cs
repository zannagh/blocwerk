// <copyright file="ShapeReviewDecision.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>
/// The review verdict on one recognised shape. Nothing is written to a hold until promote; a
/// <see cref="Pending"/> proposal promotes like <see cref="KeepPrevious"/>.
/// </summary>
public enum ShapeReviewDecision
{
    /// <summary>Not reviewed yet. Promote leaves the hold's shape as it is.</summary>
    Pending = 0,

    /// <summary>Use the recognised outline.</summary>
    Accepted = 1,

    /// <summary>Use the outline the reviewer adjusted by hand (stored on the proposal).</summary>
    Adjusted = 2,

    /// <summary>Drop any outline: the hold renders as its plain circle.</summary>
    Circle = 3,

    /// <summary>
    /// Keep the shape the hold would have had without this step: its own staged outline, or the old hold's
    /// outline the carryover warps onto it.
    /// </summary>
    KeepPrevious = 4,
}
