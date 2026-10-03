// <copyright file="PanelCameraEstimate.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Footprints;

/// <summary>How a hold photo's camera centre was estimated (<see cref="PanelCameraEstimator"/>), for the log.</summary>
/// <param name="Centre">The camera centre, world mm; null when none passed the checks.</param>
/// <param name="Method">"dlt", "planar (EXIF focal)" or "planar (self-calibrated focal)"; "none" when rejected.</param>
/// <param name="Points">Placed holds of the photo.</param>
/// <param name="Used">Holds the accepted fit kept.</param>
/// <param name="DistanceMm">The centre's distance in front of the photo's main facet (along its normal).</param>
/// <param name="MedianErrorPx">Median reprojection error of the placed holds, px (normalised for a DLT without a photo size).</param>
/// <param name="FocalPx">The focal length the planar pose used, px.</param>
/// <param name="Rejection">Why no centre was accepted (the last reason), or null.</param>
public sealed record PanelCameraEstimate(
    double[]? Centre, string Method, int Points, int Used, double? DistanceMm, double? MedianErrorPx, double? FocalPx, string? Rejection)
{
    /// <summary>A rejection.</summary>
    /// <param name="points">Placed holds of the photo.</param>
    /// <param name="reason">Why.</param>
    /// <returns>The estimate without a centre.</returns>
    public static PanelCameraEstimate Rejected(int points, string reason) => new(null, "none", points, 0, null, null, null, reason);
}
