using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Configuration;

/// <summary>
/// Hold detection and the post-detection enrichment passes. Bound from <c>Blocwerk:HoldDetection:*</c>,
/// falling back to <c>HOLDDETECTION__*</c> environment variables.
/// </summary>
public class HoldDetectionSettings
{
    public string ModelPath { get; set; } = "models/climbingcrux.onnx";

    /// <summary>
    /// Kill switch for hold outlines + fingerprints at ingest (every wall). Default true; config
    /// <c>HoldDetection:Outlines:Enabled</c>, env <c>HOLDDETECTION__OUTLINES__ENABLED</c>.
    /// </summary>
    public bool OutlinesEnabled { get; set; } = true;

    /// <summary>
    /// Kill switch for glyph (ArUco) marker detection, observations and metric hold fields at ingest.
    /// Default true, and it still only ever runs for walls with <c>Wall.GlyphsEnabled</c>. Config
    /// <c>HoldDetection:Markers:Enabled</c>, env <c>HOLDDETECTION__MARKERS__ENABLED</c>.
    /// </summary>
    public bool MarkersEnabled { get; set; } = true;

    /// <summary>
    /// Tiled YOLO detection (on by default). Config <c>HoldDetection:Tiling:*</c>, env
    /// <c>HOLDDETECTION__TILING__*</c>; see <see cref="HoldDetectionTilingSettings"/>.
    /// </summary>
    public HoldDetectionTilingSettings Tiling { get; set; } = new();

    /// <summary>Binds the settings from the <c>Blocwerk</c> configuration section and the environment.</summary>
    /// <param name="section">The <c>Blocwerk</c> section.</param>
    /// <returns>The bound settings.</returns>
    public static HoldDetectionSettings Bind(IConfiguration section) => new()
    {
        ModelPath = section["HoldDetection:ModelPath"]
                    ?? Environment.GetEnvironmentVariable("HOLDDETECTION__MODELPATH")
                    ?? "models/climbingcrux.onnx",
        OutlinesEnabled = ParseSwitch(section["HoldDetection:Outlines:Enabled"], "HOLDDETECTION__OUTLINES__ENABLED"),
        MarkersEnabled = ParseSwitch(section["HoldDetection:Markers:Enabled"], "HOLDDETECTION__MARKERS__ENABLED"),
        Tiling = HoldDetectionTilingSettings.Bind(section),
    };

    /// <summary>A default-on switch: only an explicit, parseable "false" turns it off.</summary>
    private static bool ParseSwitch(string? sectionValue, string envName) =>
        !bool.TryParse(sectionValue ?? Environment.GetEnvironmentVariable(envName), out var value) || value;
}
