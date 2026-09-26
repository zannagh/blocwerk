using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Services;

/// <summary>The active model plus what the settings card shows about it.</summary>
/// <param name="Model">The model row.</param>
/// <param name="MarkerCount">Its markers.</param>
/// <param name="MarkerSizeMm">Its printed marker size.</param>
/// <param name="Facets">One row per facet.</param>
/// <param name="Checks">The solver's checks.</param>
/// <param name="World">The document's <c>world</c> block (frame source, gravity, scale), when it has one.</param>
public sealed record ActiveWallGeometry(
    WallGeometryHistoryEntry Model,
    int MarkerCount,
    double MarkerSizeMm,
    IReadOnlyList<WallGeometryFacetRow> Facets,
    IReadOnlyList<WallGeometryModelCheck>? Checks = null,
    WallGeometryWorld? World = null)
{
    /// <summary>True for a model solved from photo features (no markers).</summary>
    public bool FromFeatures => World?.IsFeatureFrame == true;

    /// <summary>True when the millimetres are an estimate: show them with "≈".</summary>
    public bool ScaleIsEstimate => World?.ScaleIsEstimate == true;

    /// <summary>False when "up" is unknown, so absolute angles were not measured and are hidden.</summary>
    public bool AnglesKnown => World?.GravityKnown != false;
}
