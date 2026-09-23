using Blocwerk.Core.Abstractions;

namespace Blocwerk.HoldDetection.Markers;

/// <summary>How an edge profile's gradient peak is located to sub-pixel precision.</summary>
public enum EdgeSubPixelMethod
{
    /// <summary>
    /// Gradient-weighted centroid of the peak's half-maximum run (default). Unbiased on
    /// near-axis-aligned sides.
    /// </summary>
    PeakCentroid,

    /// <summary>
    /// 3-point parabola through the peak, exactly as tools/glyph/geometry/refine.py. On bilinearly
    /// sampled profiles the gradient is flat within a pixel, so this snaps edge samples to pixel
    /// cells: a 1 px staircase along near-axis-aligned sides, up to ~1 px corner error.
    /// </summary>
    Parabola,
}

/// <summary>What <see cref="MarkerCornerRefiner"/> did with one corner.</summary>
public enum CornerRefinementStatus
{
    /// <summary>Replaced by the intersection of the two fitted black-square edge lines.</summary>
    Refined,

    /// <summary>Kept: the caller marked the corner as reconstructed, not observed.</summary>
    KeptSynthetic,

    /// <summary>Kept: the corner lies outside the image (the marker is cut by the frame).</summary>
    KeptOutsideImage,

    /// <summary>Kept: the marker is too small for the edge profiles to stay inside the black border.</summary>
    KeptMarkerTooSmall,

    /// <summary>Kept: at least one adjacent side had too few consistent edge samples for a line.</summary>
    KeptEdgeFitFailed,

    /// <summary>Kept: the two adjacent fitted lines are (numerically) parallel.</summary>
    KeptParallelSides,

    /// <summary>Kept: the intersection moved the corner by more than a quarter of the marker side.</summary>
    KeptShiftTooLarge,
}

/// <summary>Outcome of refining one marker's corners (all in full-resolution pixels, TL, TR, BR, BL).</summary>
/// <param name="Corners">Refined corners; the original where the status is not <see cref="CornerRefinementStatus.Refined"/>.</param>
/// <param name="Status">Per-corner outcome, with the reason when the original was kept.</param>
/// <param name="ShiftPx">Per-corner distance between the original and the returned corner.</param>
/// <param name="ResidualPx">
/// RMS perpendicular distance of the inlier edge samples to their fitted side lines, over all sides
/// that produced a line; null when no side did.
/// </param>
public sealed record MarkerCornerRefinement(
    IReadOnlyList<MarkerPoint> Corners,
    IReadOnlyList<CornerRefinementStatus> Status,
    IReadOnlyList<double> ShiftPx,
    double? ResidualPx)
{
    /// <summary>Number of corners that were actually refined.</summary>
    public int RefinedCount => Status.Count(s => s == CornerRefinementStatus.Refined);
}
