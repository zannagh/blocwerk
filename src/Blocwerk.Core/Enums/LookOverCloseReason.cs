// <copyright file="LookOverCloseReason.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>What happened outside the look-over that left a finding with nothing to answer.</summary>
public enum LookOverCloseReason
{
    /// <summary>The hold was removed in the hold editor.</summary>
    RemovedInEditor = 0,

    /// <summary>The hold was merged into another one in the duplicate-holds review.</summary>
    MergedInEditor = 1,

    /// <summary>The hold was edited, marked or confirmed in the hold editor: a person looked at it.</summary>
    CheckedInEditor = 2,

    /// <summary>A hold was added by hand where a detection had been left out.</summary>
    HoldAddedHere = 3,
}
