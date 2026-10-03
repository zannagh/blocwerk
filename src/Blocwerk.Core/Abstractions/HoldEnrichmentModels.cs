using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Abstractions;

/// <summary>One ingested photo and the holds just detected on it.</summary>
/// <param name="Image">The encoded photo — the same bytes hold detection ran on.</param>
/// <param name="Wall">The wall (its <see cref="Wall.GlyphsEnabled"/> and <see cref="Wall.MarkerSizeMm"/> decide the marker pass).</param>
/// <param name="Holds">The freshly built holds, already added to the context.</param>
/// <param name="PanelId">The panel the photo belongs to; null for a legacy single-image upload (no observations are stored).</param>
/// <param name="PanelGeneration">The panel generation the photo belongs to (observations are keyed on it).</param>
/// <param name="FromStagedPhoto">True when the photo is the panel's staged photo, false for its live one.</param>
public sealed record HoldEnrichmentRequest(
    byte[] Image,
    Wall Wall,
    IReadOnlyList<Hold> Holds,
    Guid? PanelId = null,
    int PanelGeneration = 0,
    bool FromStagedPhoto = false);

/// <summary>What one enrichment run did.</summary>
public sealed record HoldEnrichmentSummary
{
    /// <summary>A run that changed nothing (skipped, or failed and rolled back).</summary>
    public static HoldEnrichmentSummary None { get; } = new();

    /// <summary>Gets a value indicating whether the run failed (and therefore changed nothing).</summary>
    public bool Failed { get; init; }

    /// <summary>Gets the number of holds that got a traced contour.</summary>
    public int Contours { get; init; }

    /// <summary>Gets the number of holds outlined as a circle fallback (fingerprint only).</summary>
    public int CircleFallbacks { get; init; }

    /// <summary>Gets a value indicating whether the marker pass ran (glyph wall, switch on).</summary>
    public bool MarkerPassRan { get; init; }

    /// <summary>Gets the number of validated markers in the photo.</summary>
    public int Markers { get; init; }

    /// <summary>Gets the number of observation rows written (0 for a legacy upload).</summary>
    public int Observations { get; init; }

    /// <summary>Gets the number of holds that got metric sizes.</summary>
    public int Measured { get; init; }

    /// <summary>Gets the number of holds that got a facet and plane position.</summary>
    public int Placed { get; init; }

    /// <summary>
    /// Gets the auto-detected holds that sat on a printed marker sheet and were taken back out of the
    /// context (glyph walls only). Callers that keep their own list of the fresh holds drop these from it.
    /// </summary>
    public IReadOnlyList<Hold> DroppedMarkerHolds { get; init; } = [];
}
