// <copyright file="SurfaceTurn.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>Which way a surface is turned about the vertical axis relative to the root surface.</summary>
public enum SurfaceTurn
{
    /// <summary>
    /// You turn left to face it square-on (a side wall on your left, ≈ 90°); its face points to your
    /// right. A positive yaw: counter-clockwise seen from above.
    /// </summary>
    Left,

    /// <summary>Faces the same way as the root surface, within <see cref="SurfaceYaw.StraightToleranceDeg"/>.</summary>
    Straight,

    /// <summary>You turn right to face it (a side wall on your right, ≈ 90°); a negative yaw.</summary>
    Right,
}
