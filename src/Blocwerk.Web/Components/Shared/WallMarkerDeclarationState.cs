using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The per-update marker declaration ("do these photos include ArUco markers?") shared by the big
/// wall update and the single-panel add: loaded from the wall's current setting as the default, and
/// written back only when the user changed it — BEFORE the photos are staged, so the staging calls
/// keep their signatures and simply read the wall's up-to-date setting.
/// </summary>
internal sealed class WallMarkerDeclarationState
{
    /// <summary>
    /// The checkbox text for an update or panel add: the answer is saved on the WALL, so unticking it
    /// switches markers off for every panel, not just the photos being added.
    /// </summary>
    public const string WholeWallLabel = "This wall has ArUco markers (applies to the whole wall)";

    /// <summary>The warning shown while the user is about to switch markers off on a wall that has them.</summary>
    public const string TurningOffHint =
        "Unticking this switches markers off for the whole wall, not just these photos. Saved when you continue.";

    private Guid loadedFor;
    private WallGlyphSettings? stored;

    /// <summary>True once the wall's setting is known; the control is only shown then.</summary>
    public bool Loaded => stored is not null;

    public bool Enabled { get; set; }

    public double SizeMm { get; set; } = WallGlyphSettings.DefaultMarkerSizeMm;

    /// <summary>True while the box is unticked on a wall whose stored setting has markers on.</summary>
    public bool TurningOff => stored is { Enabled: true } && !Enabled;

    /// <summary>The hint to show: the whole-wall warning while turning markers off, otherwise <paramref name="normal"/>.</summary>
    public string HintFor(string normal) => TurningOff ? TurningOffHint : normal;

    /// <summary>Loads the wall's setting once per wall. A failure leaves the control hidden and the setting untouched.</summary>
    public async Task LoadAsync(IWallGlyphService glyphs, Guid wallId, ILogger logger)
    {
        if (wallId == Guid.Empty || wallId == loadedFor)
        {
            return;
        }

        loadedFor = wallId;
        try
        {
            stored = await glyphs.GetGlyphSettingsAsync(wallId);
            Enabled = stored.Enabled;
            SizeMm = stored.MarkerSizeMm ?? WallGlyphSettings.DefaultMarkerSizeMm;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            // Nothing to default to: the update runs on the wall's stored setting, as before markers existed.
            logger.LogWarning(ex, "Could not load the marker setting of wall {WallId}", wallId);
        }
    }

    /// <summary>
    /// Saves the declaration when it differs from the wall's setting. Returns an error message for the
    /// user when that fails — the caller must then NOT stage, so an update never runs on a setting the
    /// user did not choose — or null to go ahead.
    /// </summary>
    public async Task<string?> ApplyAsync(IWallGlyphService glyphs, Guid wallId, ILogger logger)
    {
        if (stored is null || !Differs(stored))
        {
            return null;
        }

        try
        {
            stored = await glyphs.SetGlyphSettingsAsync(wallId, Enabled, Enabled ? SizeMm : null);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or UnauthorizedAccessException or KioskRestrictedException)
        {
            logger.LogWarning(ex, "Could not save the marker declaration of wall {WallId}", wallId);
            return $"Could not save the marker setting: {ex.Message}";
        }
    }

    private bool Differs(WallGlyphSettings current) =>
        Enabled != current.Enabled
        || (Enabled && SizeMm != (current.MarkerSizeMm ?? WallGlyphSettings.DefaultMarkerSizeMm));
}
