// <copyright file="SurfaceLean.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>Which way a surface leans, as a climber standing in front of it sees it.</summary>
public enum SurfaceLean
{
    /// <summary>Leans away from you (the top is further away than the bottom); a negative overhang angle.</summary>
    Slab,

    /// <summary>Plumb, within <see cref="SurfaceAngle.VerticalToleranceDeg"/>.</summary>
    Vertical,

    /// <summary>Leans over you (the top sticks out); a positive overhang angle.</summary>
    Overhang,
}
