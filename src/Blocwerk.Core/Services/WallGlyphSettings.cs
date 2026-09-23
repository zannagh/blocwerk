namespace Blocwerk.Core.Services;

/// <summary>A wall's marker declaration.</summary>
public sealed record WallGlyphSettings(bool Enabled, double? MarkerSizeMm)
{
    /// <summary>The size the UI suggests when markers are switched on without one.</summary>
    public const double DefaultMarkerSizeMm = 125.0;
}
