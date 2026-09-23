using SkiaSharp;

namespace Blocwerk.HoldDetection.Tiling;

/// <summary>Maps the configured sampling name to the Skia options YoloDotNet letterboxes with.</summary>
internal static class YoloSampling
{
    /// <summary>YoloDotNet's own default: nearest neighbour, no mipmaps.</summary>
    public static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    /// <summary>Parses <c>nearest</c>, <c>linear</c> or <c>mipmap</c>; anything else is nearest.</summary>
    /// <param name="name">The configured name.</param>
    /// <returns>The sampling options.</returns>
    public static SKSamplingOptions Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "linear" => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
        "mipmap" => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
        _ => Nearest,
    };
}
