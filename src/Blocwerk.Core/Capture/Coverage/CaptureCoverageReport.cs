// <copyright file="CaptureCoverageReport.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// What a capture saw of the wall and what the next capture should add (<see cref="CaptureCoverageAnalyzer"/>):
/// per facet a grid of cells rated by how the solved cameras see them, per volume its faces, per facet its markers,
/// the walk-along video against the capture recipe, and the plain-words "what to add" list. Stored on
/// <see cref="Entities.WallCapture.CoverageJson"/> and served by the capture API.
/// </summary>
/// <param name="Version">Report format version.</param>
/// <param name="CaptureId">The capture.</param>
/// <param name="ModelId">The model whose cameras and geometry were rated.</param>
/// <param name="ComputedAt">When it was computed.</param>
/// <param name="CellMm">The cell side of the facet grids, mm.</param>
/// <param name="PhotoViews">Solved photo cameras used.</param>
/// <param name="VideoViews">Registered video frames with a pose used (0 when their poses are not reported).</param>
/// <param name="Advice">The "Next capture: what to add" list, most important first.</param>
/// <param name="Facets">Per facet: the cell grid and the markers.</param>
/// <param name="Volumes">Per volume: its faces.</param>
/// <param name="Video">The walk-along video against the recipe.</param>
public sealed record CaptureCoverageReport(
    int Version,
    Guid CaptureId,
    Guid ModelId,
    DateTimeOffset ComputedAt,
    double CellMm,
    int PhotoViews,
    int VideoViews,
    IReadOnlyList<CoverageAdvice> Advice,
    IReadOnlyList<FacetCoverage> Facets,
    IReadOnlyList<VolumeCoverage> Volumes,
    VideoCoverage Video)
{
    /// <summary>The current format.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The report as stored and served.</summary>
    /// <returns>JSON.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>A stored report, or null when there is none or it does not parse.</summary>
    /// <param name="json">The stored JSON.</param>
    /// <returns>The report.</returns>
    public static CaptureCoverageReport? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<CaptureCoverageReport>(json, Json);
            return report?.Version == CurrentVersion ? report : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One line of the "what to add" list.</summary>
/// <param name="Kind">What it is about: <c>volume</c>, <c>surface</c>, <c>markers</c> or <c>video</c>.</param>
/// <param name="Text">The plain-words tip, e.g. "Shoot the undersides of the 3 volumes on the right of the main wall from below".</param>
/// <param name="FacetId">The facet it is about, when one.</param>
public sealed record CoverageAdvice(string Kind, string Text, string? FacetId = null);

/// <summary>A facet's view coverage and markers.</summary>
/// <param name="FacetId">The model's facet id.</param>
/// <param name="Name">Its segment name ("Main wall", or "Kickboard (1a)" for a folded segment).</param>
/// <param name="ALo">The grid's lower a edge, mm.</param>
/// <param name="BLo">The grid's lower b edge, mm.</param>
/// <param name="CellMm">Cell side, mm.</param>
/// <param name="Cols">Cells along a.</param>
/// <param name="Rows">Cells along b.</param>
/// <param name="Cells">One <see cref="CoverageCellCodes"/> character per cell, row-major along a, bottom row first.</param>
/// <param name="Counts">How many cells have each rating.</param>
/// <param name="Markers">The facet's markers.</param>
public sealed record FacetCoverage(
    string FacetId,
    string Name,
    double ALo,
    double BLo,
    double CellMm,
    int Cols,
    int Rows,
    string Cells,
    CoverageCounts Counts,
    MarkerCoverage Markers);

/// <summary>How many cells (or samples) have each rating; a cell counts once, under its most urgent rating.</summary>
/// <param name="Total">Rated cells (cells under a volume or inside the wall are not rated).</param>
/// <param name="Good">Seen well.</param>
/// <param name="FewDirections">Seen from fewer than 3 directions.</param>
/// <param name="Grazing">Only seen at a steep angle (more than 70° off face-on).</param>
/// <param name="LowResolution">Only seen from far away (coarser than 2 mm per pixel).</param>
/// <param name="Never">Seen by no camera.</param>
public sealed record CoverageCounts(int Total, int Good, int FewDirections, int Grazing, int LowResolution, int Never)
{
    /// <summary>Cells that are not <see cref="Good"/>.</summary>
    [JsonIgnore]
    public int Weak => Total - Good;
}

/// <summary>A facet's markers as the capture saw them.</summary>
/// <param name="Markers">Each marker with the photos it was seen in.</param>
/// <param name="SpreadRatio">Marker centres' bounding box relative to the facet (0–1): small = poorly pinned down.</param>
/// <param name="FewMarkers">Fewer than 3 markers.</param>
/// <param name="PoorSpread">The markers are bunched on a large facet.</param>
/// <param name="WeakMarkers">Markers seen in fewer than 3 photos.</param>
/// <param name="Suggestion">Where to add markers, or null.</param>
public sealed record MarkerCoverage(
    IReadOnlyList<MarkerSeen> Markers,
    double SpreadRatio,
    bool FewMarkers,
    bool PoorSpread,
    IReadOnlyList<int> WeakMarkers,
    MarkerSuggestion? Suggestion);

/// <summary>One marker: id, photos it was seen in, centre on the facet (mm).</summary>
/// <param name="Id">Marker id.</param>
/// <param name="Photos">Photos it was seen in.</param>
/// <param name="A">Centre along u, mm.</param>
/// <param name="B">Centre along v, mm.</param>
public sealed record MarkerSeen(int Id, int Photos, double A, double B);

/// <summary>Where to add markers on a facet.</summary>
/// <param name="Count">How many.</param>
/// <param name="SizeMm">Printed size from the marker sizing rules for this facet and the capture's cameras.</param>
/// <param name="Points">Suggested centres [a, b] on the facet, mm.</param>
/// <param name="Where">The region in words, e.g. "the bottom left".</param>
public sealed record MarkerSuggestion(int Count, double SizeMm, IReadOnlyList<double[]> Points, string Where);

/// <summary>A volume's faces as the capture saw them.</summary>
/// <param name="Index">The volume's number ("Volume 3").</param>
/// <param name="FacetId">The facet it stands on.</param>
/// <param name="Footprint">Its outline on the facet, [a, b] mm.</param>
/// <param name="Faces">Each face that has surface.</param>
public sealed record VolumeCoverage(int Index, string FacetId, IReadOnlyList<double[]> Footprint, IReadOnlyList<VolumeFaceCoverage> Faces);

/// <summary>One face of a volume: its surface samples' ratings.</summary>
/// <param name="Face">Which face.</param>
/// <param name="Counts">Sample ratings.</param>
/// <param name="Status">The face's rating: good, or its most common weakness when enough of it is weak.</param>
public sealed record VolumeFaceCoverage(VolumeFace Face, CoverageCounts Counts, CoverageCellStatus Status);

/// <summary>The walk-along video against the capture recipe.</summary>
/// <param name="HasVideo">A video was uploaded with the capture.</param>
/// <param name="FramesExtracted">Frames taken from it.</param>
/// <param name="FramesRegistered">Frames the photo-real stage could place; null when not reported.</param>
/// <param name="PosesFrom">Whose camera positions the recipe was checked on.</param>
/// <param name="Passes">Each part of the recipe and whether it is there.</param>
public sealed record VideoCoverage(
    bool HasVideo, int FramesExtracted, int? FramesRegistered, CoveragePoseSource PosesFrom, IReadOnlyList<RecipePass> Passes);

/// <summary>One part of the capture recipe.</summary>
/// <param name="Key">Stable key: knee, chest, overhead, arc-left, arc-right, under-volume-N.</param>
/// <param name="Label">In words, e.g. "overhead pass aimed down".</param>
/// <param name="Present">Whether the cameras cover it.</param>
/// <param name="Detail">What was found, e.g. "4 views".</param>
public sealed record RecipePass(string Key, string Label, bool Present, string Detail);
