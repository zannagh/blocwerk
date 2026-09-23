// <copyright file="NetTransform.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>Maps a segment's own frame into the net: rotate counter-clockwise, then translate.</summary>
/// <param name="RotationRad">Counter-clockwise rotation in radians.</param>
/// <param name="Origin">Net position of the segment frame's origin.</param>
public readonly record struct NetTransform(double RotationRad, PlanVector Origin)
{
    /// <summary>Maps a point of the segment frame into the net.</summary>
    public PlanVector Apply(PlanVector local) => Origin + local.Rotate(RotationRad);

    /// <summary>Maps a direction of the segment frame into the net.</summary>
    public PlanVector ApplyDirection(PlanVector local) => local.Rotate(RotationRad);

    /// <summary>Gets the rotation in degrees, normalised to (-180, 180].</summary>
    public double RotationDeg
    {
        get
        {
            var deg = RotationRad * 180.0 / Math.PI % 360.0;
            if (deg <= -180)
            {
                deg += 360;
            }
            else if (deg > 180)
            {
                deg -= 360;
            }

            return Math.Abs(deg) < 1e-9 ? 0 : deg;
        }
    }
}
