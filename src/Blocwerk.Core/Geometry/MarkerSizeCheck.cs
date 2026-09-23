// <copyright file="MarkerSizeCheck.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;

namespace Blocwerk.Core.Geometry;

/// <summary>
/// Planned vs. measured marker size (<see cref="WallGeometryMarker.MeasuredSideMm"/>): a sheet printed at
/// another size than the plan says distorts the whole solve, so it is named with both sizes.
/// </summary>
public static class MarkerSizeCheck
{
    /// <summary>A difference up to this many mm is measurement noise.</summary>
    public const double ToleranceMm = 5;

    /// <summary>A difference up to this share of the planned size is measurement noise.</summary>
    public const double ToleranceShare = 0.05;

    /// <summary>The measured size when it differs from the planned one by more than max(5 mm, 5 %); else null.</summary>
    public static double? Mismatch(double? plannedMm, double? measuredMm)
    {
        if (plannedMm is not { } planned || measuredMm is not { } measured || planned <= 0)
        {
            return null;
        }

        return Math.Abs(measured - planned) > Math.Max(ToleranceMm, ToleranceShare * planned) ? measured : null;
    }

    /// <summary>The measured size of <paramref name="marker"/> when it clearly differs from <paramref name="plannedMm"/>.</summary>
    public static double? Mismatch(WallGeometryMarker? marker, double? plannedMm) =>
        marker is null ? null : Mismatch(plannedMm, marker.MeasuredSideMm);

    /// <summary>E.g. "Marker 4 was planned at 100 mm but measures ≈125 mm in the photos; check its printed size".</summary>
    public static string Describe(int id, double plannedMm, double measuredMm) => string.Create(
        CultureInfo.InvariantCulture,
        $"Marker {id} was planned at {plannedMm:0} mm but measures ≈{measuredMm:0} mm in the photos; check its printed size.");
}
