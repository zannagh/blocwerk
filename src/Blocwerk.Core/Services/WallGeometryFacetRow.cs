namespace Blocwerk.Core.Services;

/// <summary>One facet row of the active model, as the settings card lists it.</summary>
public sealed record WallGeometryFacetRow(
    string FacetId,
    int SegmentIndex,
    string? SegmentName,
    double? DeclaredAngleDeg,
    double? MeasuredAngleDeg,
    double? YawDeg,
    int MarkerCount);
