// <copyright file="HoldMoveOutcome.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>What a measured hold move does to the boulders that use the hold.</summary>
public enum HoldMoveOutcome
{
    /// <summary>Within measurement noise: the hold stayed. Nothing is flagged.</summary>
    Stayed = 0,

    /// <summary>Moved or turned, but within the cutoff: the boulder keeps the hold and is marked for review.</summary>
    Kept = 1,

    /// <summary>Moved beyond the cutoff: the hold is taken off the boulder, which is marked for revision.</summary>
    Removed = 2,

    /// <summary>
    /// Measured beyond the cutoff but not certain enough to take the hold off (a provisional or low-confidence measurement,
    /// or not confirmed by a person): the boulder keeps the hold and is marked for review ("possibly moved, check").
    /// </summary>
    Possible = 3,
}
