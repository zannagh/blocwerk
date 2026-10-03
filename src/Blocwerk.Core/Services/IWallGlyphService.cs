namespace Blocwerk.Core.Services;

/// <summary>
/// The opt-in glyph (printed ArUco marker) settings of a wall and its solved geometry models
/// (<c>wall-geometry.json</c>). A wall without markers never touches any of this. Every mutation is
/// gated like the other wall-admin actions (owner or admin, never from a kiosk tablet).
/// </summary>
public interface IWallGlyphService
{
    /// <summary>The wall's marker declaration, readable by anyone who can see the wall.</summary>
    Task<WallGlyphSettings> GetGlyphSettingsAsync(Guid wallId);

    /// <summary>
    /// Declares whether the wall carries printed markers and how big they are (black-square side, mm).
    /// A null size keeps the stored one; a size outside (0, 1000] mm is refused.
    /// </summary>
    Task<WallGlyphSettings> SetGlyphSettingsAsync(Guid wallId, bool enabled, double? markerSizeMm);

    /// <summary>
    /// Validates <paramref name="json"/> as a <c>wall-geometry.json</c> and, when usable, stores it as the
    /// wall's new ACTIVE model (the previous one stays as inactive history), refreshes the summary
    /// columns and copies the measured angles onto segments bound to a marker segment. Never throws for
    /// a bad file: problems come back in <see cref="GeometryImportResult.Errors"/>.
    /// </summary>
    Task<GeometryImportResult> ImportGeometryAsync(Guid wallId, string json, string? notes);

    /// <summary>The active model with its facet table, or null when none was imported. Admin only.</summary>
    Task<ActiveWallGeometry?> GetActiveGeometryAsync(Guid wallId);

    /// <summary>Every model the wall ever had, newest first, without the JSON payload. Admin only.</summary>
    Task<IReadOnlyList<WallGeometryHistoryEntry>> GetGeometryHistoryAsync(Guid wallId);

    /// <summary>Makes an older model the active one again and re-applies its measured angles.</summary>
    Task ActivateGeometryAsync(Guid modelId);

    /// <summary>
    /// Binds a wall segment to a marker segment (the solved segment index: plan segment, or <c>markerId / 6</c>), or unbinds it with null. The
    /// active model's measured angle and yaw are copied on at once.
    /// </summary>
    Task SetSegmentMarkerIndexAsync(Guid segmentId, int? markerSegmentIndex);
}
