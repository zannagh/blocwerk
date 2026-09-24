using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Services;

/// <summary>The active model plus what the settings card shows about it.</summary>
public sealed record ActiveWallGeometry(
    WallGeometryHistoryEntry Model,
    int MarkerCount,
    double MarkerSizeMm,
    IReadOnlyList<WallGeometryFacetRow> Facets,
    IReadOnlyList<WallGeometryModelCheck>? Checks = null);
