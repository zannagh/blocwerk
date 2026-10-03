using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Configuration;

/// <summary>
/// Tiled YOLO hold detection. The model's input is 640 px, so a whole 4032 px photo is shrunk ~6x and most
/// small holds (feet, kickboard) vanish; tiling runs the model on overlapping native-resolution windows
/// instead. Bound from <c>Blocwerk:HoldDetection:Tiling:*</c>, falling back to
/// <c>HOLDDETECTION__TILING__*</c> environment variables. <see cref="Enabled"/> false restores the old
/// whole-image path (conf 0.25) without a deploy.
/// </summary>
public class HoldDetectionTilingSettings
{
    /// <summary>Gets or sets a value indicating whether tiled detection runs. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the square window edge in source pixels. Default 1280.</summary>
    public int TileSize { get; set; } = 1280;

    /// <summary>Gets or sets the overlap between neighbouring windows in source pixels. Default 256.</summary>
    public int Overlap { get; set; } = 256;

    /// <summary>
    /// Gets or sets the minimum YOLO confidence for a tiled detection. Default 0.35: on The Attic it keeps the
    /// recall of 0.25 (589/634) with ~9 % fewer false positives; 0.5 starts losing small holds.
    /// </summary>
    public double Confidence { get; set; } = 0.35;

    /// <summary>
    /// Gets or sets how each window is resampled into the model's 640 px input: <c>nearest</c> (YoloDotNet's
    /// default), <c>linear</c> or <c>mipmap</c> (linear + mipmaps). Applies to the tiled path only; the
    /// whole-image path keeps nearest. Default nearest: measured on The Attic, linear and mipmap each lost a
    /// few holds, tiled or not, so the model is best fed what it was validated with.
    /// </summary>
    public string Sampling { get; set; } = "nearest";

    /// <summary>Binds the tiling settings from the <c>Blocwerk</c> section and the environment.</summary>
    /// <param name="section">The <c>Blocwerk</c> section.</param>
    /// <returns>The bound settings.</returns>
    public static HoldDetectionTilingSettings Bind(IConfiguration section)
    {
        var defaults = new HoldDetectionTilingSettings();
        var enabled = Read(section, "Enabled", "ENABLED");
        var settings = new HoldDetectionTilingSettings
        {
            Enabled = !bool.TryParse(enabled, out var on) || on,
            TileSize = ReadInt(section, "TileSize", "TILESIZE", defaults.TileSize, 320),
            Overlap = ReadInt(section, "Overlap", "OVERLAP", defaults.Overlap, 0),
            Confidence = ReadDouble(section, "Confidence", "CONFIDENCE", defaults.Confidence),
            Sampling = Read(section, "Sampling", "SAMPLING")?.Trim().ToLowerInvariant() ?? defaults.Sampling,
        };

        // A window must advance: an overlap at or above the tile size would never finish the row.
        if (settings.Overlap >= settings.TileSize / 2)
        {
            settings.Overlap = settings.TileSize / 4;
        }

        return settings;
    }

    private static string? Read(IConfiguration section, string key, string env) =>
        section[$"HoldDetection:Tiling:{key}"] ?? Environment.GetEnvironmentVariable($"HOLDDETECTION__TILING__{env}");

    private static int ReadInt(IConfiguration section, string key, string env, int fallback, int min) =>
        int.TryParse(Read(section, key, env), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min
            ? value
            : fallback;

    private static double ReadDouble(IConfiguration section, string key, string env, double fallback) =>
        double.TryParse(Read(section, key, env), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is > 0 and < 1
            ? value
            : fallback;
}
