// <copyright file="HoldLook.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>
/// Whether the detection an old hold was paired with still looks like that hold (<see cref="Services.HoldLookCheck"/>).
/// Ordered from the least to the most drastic, so the least drastic of several verdicts is their minimum.
/// </summary>
public enum HoldLook
{
    /// <summary>The new photo shows the old hold's look at the detection: the pair stands.</summary>
    Same = 0,

    /// <summary>Not clear: the pair stands and a person is asked.</summary>
    Unsure = 1,

    /// <summary>The new photo shows something else there: another hold took the old hold's place.</summary>
    Replaced = 2,
}
