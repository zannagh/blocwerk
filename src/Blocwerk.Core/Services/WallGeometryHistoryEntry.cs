namespace Blocwerk.Core.Services;

/// <summary>
/// A stored model without its JSON payload. <see cref="FilesRemovedAt"/>: when the capture retention deleted its wall
/// textures and photo-real view (activating it brings back the geometry only); null while it has them.
/// </summary>
public sealed record WallGeometryHistoryEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    bool IsActive,
    int SchemaVersion,
    string Source,
    double? ReprojRmsPx,
    double? WidthMm,
    double? HeightMm,
    string? Notes,
    DateTimeOffset? FilesRemovedAt = null);
