// <copyright file="ReviewGroup.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>The groups of the one review, in the order they are asked about.</summary>
public enum ReviewGroup
{
    /// <summary>An old hold carried onto a detection the matcher or the look check was unsure about.</summary>
    Unsure = 0,

    /// <summary>A hand-placed old hold merged onto a detection, or several detections competing for it.</summary>
    HandPlaced = 1,

    /// <summary>Another hold sits where the old one was.</summary>
    Replaced = 2,

    /// <summary>An old hold where the new photo and the 3D model show bare wall.</summary>
    PossiblyRemoved = 3,

    /// <summary>A left-out detection the 3D model sees a hold at.</summary>
    Conflicting3D = 4,

    /// <summary>An old hold not found again, kept in place.</summary>
    NotFound = 5,

    /// <summary>A detection the triage left out.</summary>
    LeftOut = 6,

    /// <summary>A new detection that goes live as a new hold.</summary>
    NewHold = 7,

    /// <summary>A "this hold may have moved" suggestion.</summary>
    Moved = 8,
}
