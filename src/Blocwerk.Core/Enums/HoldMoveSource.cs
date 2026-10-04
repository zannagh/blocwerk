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
}
