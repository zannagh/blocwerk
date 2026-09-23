namespace Blocwerk.Core.Services;

/// <summary>A stored model without its JSON payload.</summary>
public sealed record WallGeometryHistoryEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    bool IsActive,
    int SchemaVersion,
    string Source,
    double? ReprojRmsPx,
    double? WidthMm,
    double? HeightMm,
    string? Notes);
