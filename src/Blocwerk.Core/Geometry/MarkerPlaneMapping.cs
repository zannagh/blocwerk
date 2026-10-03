using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Geometry;

/// <summary>All facet mappings recoverable from one photo.</summary>
public sealed record MarkerPlaneMapping
{
    /// <summary>One mapping per facet that has at least one usable marker in the photo.</summary>
    public required IReadOnlyList<FacetPlaneMap> Facets { get; init; }

    /// <summary>Detected ids the geometry document does not know (not placed / not solved).</summary>
    public IReadOnlyList<int> UnknownMarkerIds { get; init; } = [];

    /// <summary>Known ids whose corners were RANSAC outliers against the other markers of their facet.</summary>
    public IReadOnlyList<int> OutlierMarkerIds { get; init; } = [];

    /// <summary>The mapping for <paramref name="facetId"/>, or null when the photo does not show it.</summary>
    public FacetPlaneMap? Facet(string facetId) => Facets.FirstOrDefault(f => f.FacetId == facetId);
}

/// <summary>Construction data for a <see cref="FacetPlaneMap"/>.</summary>
internal sealed record FacetPlaneMapInfo(
    int SegmentIndex,
    string? FacetId,
    bool IsLocalFrame,
    IReadOnlyList<int> MarkerIds,
    double CornerReprojRmsPx,
    int InlierCornerCount,
    int TotalCornerCount,
    MarkerPoint ReferencePx);
