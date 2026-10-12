// <copyright file="LookOverFindingStatus.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>Where a <see cref="Entities.WallLookOverFinding"/> stands.</summary>
public enum LookOverFindingStatus
{
    /// <summary>Nobody answered it yet.</summary>
    Open = 0,

    /// <summary>A person answered it (<see cref="Entities.WallLookOverFinding.Answer"/>).</summary>
    Settled = 1,

    /// <summary>A later panel update replaced the hold's panel before anybody answered; that update has findings of its own.</summary>
    Superseded = 2,

    /// <summary>
    /// What it was about was dealt with in the hold editor (the hold was removed, merged or checked there), so nothing
    /// is left to answer (<see cref="Entities.WallLookOverFinding.ClosedReason"/>).
    /// </summary>
    Closed = 3,
}
