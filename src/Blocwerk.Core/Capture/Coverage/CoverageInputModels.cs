// <copyright file="CoverageInputModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>What the coverage report is computed from.</summary>
/// <param name="CaptureId">The capture.</param>
/// <param name="ModelId">Its model.</param>
/// <param name="Document">The model's geometry.</param>
/// <param name="Photos">The solved photo cameras.</param>
/// <param name="VideoFrames">The registered video frames with a pose (empty when not reported).</param>
/// <param name="Volumes">The model's visible volumes.</param>
/// <param name="HoldBounds">Per facet, the bounds of the holds placed on it (they widen the facet's region).</param>
/// <param name="Video">The walk-along video's frame counts.</param>
public sealed record CoverageInputs(
    Guid CaptureId,
    Guid ModelId,
    WallGeometryDocument Document,
    IReadOnlyList<CoverageCamera> Photos,
    IReadOnlyList<CoverageCamera> VideoFrames,
    IReadOnlyList<CoverageVolume> Volumes,
    IReadOnlyDictionary<string, PlaneRectMm> HoldBounds,
    CoverageVideoInput Video);

/// <summary>The walk-along video's frame counts.</summary>
/// <param name="HasVideo">A video came with the capture.</param>
/// <param name="FramesExtracted">Frames taken from it.</param>
/// <param name="FramesRegistered">Frames the photo-real stage placed; null when not reported.</param>
public sealed record CoverageVideoInput(bool HasVideo, int FramesExtracted, int? FramesRegistered);

/// <summary>A facet to rate: its frame, the region the grid covers and its angles (for the marker sizing).</summary>
/// <param name="Id">The model's facet id.</param>
/// <param name="Name">Its segment's name, e.g. "Main wall".</param>
/// <param name="Frame">Its plane frame.</param>
/// <param name="Region">Its extent (markers and placed holds), mm.</param>
/// <param name="OverhangDeg">Tilt from vertical (positive = overhanging), or 0 when unknown.</param>
/// <param name="YawDeg">Turn relative to the reference facet, or 0.</param>
public sealed record CoverageFacet(string Id, string Name, FacetFrame Frame, PlaneRectMm Region, double OverhangDeg, double YawDeg);

/// <summary>A visible volume: its number, its facet, its height field and outline.</summary>
/// <param name="Index">Its number ("Volume 3").</param>
/// <param name="FacetId">The facet it stands on.</param>
/// <param name="Surface">Its height field over the facet.</param>
/// <param name="Footprint">Its outline, [a, b] mm.</param>
public sealed record CoverageVolume(int Index, string FacetId, VolumeSurface Surface, IReadOnlyList<double[]> Footprint);

/// <summary>A facet's rated grid.</summary>
/// <param name="Facet">The facet.</param>
/// <param name="Grid">The cell grid over its region.</param>
/// <param name="Views">Per cell, how the cameras see its centre (default for a cell under a volume).</param>
/// <param name="Status">Per cell, its rating.</param>
public sealed record RatedFacet(CoverageFacet Facet, CellGrid Grid, PointViews[] Views, CoverageCellStatus[] Status)
{
    /// <summary>The cells as <see cref="FacetCoverage.Cells"/> codes.</summary>
    public string Codes => new(Status.Select(CoverageCellCodes.Code).ToArray());
}

/// <summary>A capture looked up for its coverage report.</summary>
/// <param name="CaptureFound">False when the wall has no such capture.</param>
/// <param name="Report">The report, or null when it has not been computed (yet).</param>
public sealed record CaptureCoverageLookup(bool CaptureFound, CaptureCoverageReport? Report);
