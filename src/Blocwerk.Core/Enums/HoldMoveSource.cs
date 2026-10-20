// <copyright file="HoldMoveSource.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>Where a measured hold move comes from.</summary>
public enum HoldMoveSource
{
    /// <summary>Both holds have a placement on the same facet of the 3D model: the distance is measured on the wall.</summary>
    ThreeD = 0,

    /// <summary>
    /// No usable 3D placement for both: the distance is the photo warp's prediction error converted to millimetres with
    /// the panel's scale. Coarser, and with a larger noise floor.
    /// </summary>
    TwoD = 1,

    /// <summary>
    /// Not a move: the photos show the same hold outlined differently (see <see cref="HoldMoves.ReOutlineRule"/>), so the
    /// shift of its centre is the new outline's. Recorded as stayed; the 3D re-measurement leaves such a pair alone, because
    /// its placements come from the same two outlines.
    /// </summary>
    ReOutline = 2,
}
