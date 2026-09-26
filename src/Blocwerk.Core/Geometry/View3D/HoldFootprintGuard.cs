// <copyright file="HoldFootprintGuard.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Sanity check of a refined <see cref="HoldFootprint"/>: its outline is relative to the hold's placed centre, so its
/// centroid must lie near (0, 0). A footprint whose centroid is farther than <see cref="LimitMm"/> was built on a
/// silhouette of somewhere else (a mapping that disagrees with the placement, a neighbour traced in a capture photo)
/// and is not stored. Pure.
/// </summary>
public static class HoldFootprintGuard
{
    /// <summary>The smallest allowed centroid offset, mm (small holds, or holds of unknown size).</summary>
    public const double MinLimitMm = 60;

    /// <summary>The allowed centroid offset as a share of the hold's larger side.</summary>
    public const double SizeShare = 0.75;

    /// <summary>How far the footprint's centroid may lie from the hold's placed centre, mm.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>max(<see cref="MinLimitMm"/>, <see cref="SizeShare"/> × its larger side).</returns>
    public static double LimitMm(Hold hold) =>
        Math.Max(MinLimitMm, SizeShare * Math.Max(hold.WidthMm ?? 0, hold.HeightMm ?? 0));

    /// <summary>The distance of the footprint's centroid (vertex mean) from the hold's placed centre, mm.</summary>
    /// <param name="footprint">The footprint.</param>
    /// <returns>The offset; infinite for an empty outline.</returns>
    public static double OffsetMm(HoldFootprint footprint)
    {
        if (footprint.Outline.Count == 0)
        {
            return double.PositiveInfinity;
        }

        var a = footprint.Outline.Average(v => v[0]);
        var b = footprint.Outline.Average(v => v[1]);
        return Math.Sqrt((a * a) + (b * b));
    }

    /// <summary>Whether the footprint lies where the hold is placed.</summary>
    /// <param name="footprint">The footprint.</param>
    /// <param name="hold">Its hold.</param>
    /// <returns>True when its centroid is within <see cref="LimitMm"/>.</returns>
    public static bool Near(HoldFootprint footprint, Hold hold) => OffsetMm(footprint) <= LimitMm(hold);
}
