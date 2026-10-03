// <copyright file="NewHoldDiscardReason.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>Why an unpaired staged detection is suggested as not-a-new-hold (discarded by default).</summary>
public enum NewHoldDiscardReason
{
    /// <summary>It sits on a printed marker detected on the staged photo.</summary>
    OnMarker,

    /// <summary>The old photo of the panel did not cover that spot (off the panel, or a neighbouring wall).</summary>
    OutsideOldPhoto,

    /// <summary>The old photo shows the same thing at the aligned spot: a hold that was there but never registered.</summary>
    UnchangedSinceOldPhoto,

    /// <summary>The overlap maps it onto the centre photo, which shows that spot itself (a hold of the centre, or nothing).</summary>
    SeenOnNeighbourPanel,

    /// <summary>The 3D model has an existing hold at that spot: a second detection of a hold that is already there.</summary>
    KnownHoldIn3D,

    /// <summary>On the 3D model the spot lies well off the wall (floor mats, ceiling, a neighbouring wall).</summary>
    OffWallIn3D,
}
