// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Configuration;

namespace Blocwerk.Core.Capture;

/// <summary>How fine a re-render of a capture's wall textures is (<see cref="TextureQualityPresets"/>).</summary>
public enum TextureQuality
{
    /// <summary>The server's configured texture settings (the worker's own defaults when none are set).</summary>
    Standard = 0,

    /// <summary>Finer: 1.5 mm per pixel, the full multi-view blend.</summary>
    High = 1,

    /// <summary>Finest that still blends on the 4 GiB worker: 1.25 mm per pixel with a slimmer blend.</summary>
    Maximum = 2,
}

/// <summary>
/// The worker <c>options</c> of each <see cref="TextureQuality"/>. The multi-view blend (exposure balance, seam
/// blending) only runs when it fits <c>TEXTURES_BLEND_MAX_BYTES</c> (2 GB, so a 4 GiB worker); the presets are chosen so
/// that The Attic (356 photos, 8 facets) still blends in full at High (about 1.6 GB) and with fewer views at Maximum
/// (<see cref="TextureQualityEstimate"/> tells for any wall). A preset never makes the texture coarser than the
/// configured one.
/// </summary>
public static class TextureQualityPresets
{
    /// <summary>The worker's own defaults, used when the server configures nothing.</summary>
    public const double DefaultMmPerPx = 2.0;

    public const int DefaultMaxSidePx = 4096;

    public const int DefaultBlendViews = 6;

    private const int FineMaxSidePx = 8192;

    /// <summary>The effective texture options of a quality: mm per pixel, longest side and blend views.</summary>
    public static (double MmPerPx, int MaxSidePx, int BlendViews) Resolve(TextureQuality quality, GeometryTextureSettings configured)
    {
        var mm = configured.MmPerPx ?? DefaultMmPerPx;
        var side = configured.MaxSidePx ?? DefaultMaxSidePx;
        return quality switch
        {
            TextureQuality.High => (Math.Min(mm, 1.5), Math.Max(side, FineMaxSidePx), DefaultBlendViews),
            TextureQuality.Maximum => (Math.Min(mm, 1.25), Math.Max(side, FineMaxSidePx), 4),
            _ => (mm, side, DefaultBlendViews),
        };
    }

    /// <summary>The <c>options</c> JSON of a textures job at this quality; null for Standard with nothing configured.</summary>
    public static string? ToOptionsJson(TextureQuality quality, GeometryTextureSettings configured)
    {
        if (quality == TextureQuality.Standard)
        {
            return configured.ToOptionsJson();
        }

        var (mm, side, views) = Resolve(quality, configured);
        var options = new JsonObject { ["mmPerPx"] = mm, ["maxSidePx"] = side, ["blendViews"] = views };
        if (configured.JpegQuality is { } jpeg)
        {
            options["jpegQuality"] = jpeg;
        }

        return options.ToJsonString();
    }

    public static string Label(TextureQuality quality) => quality switch
    {
        TextureQuality.High => "High",
        TextureQuality.Maximum => "Maximum",
        _ => "Standard",
    };

    /// <summary>How much longer than Standard it takes, roughly (the pixel count and the blend work grow with it).</summary>
    public static double RelativeDuration(TextureQuality quality) => quality switch
    {
        TextureQuality.High => 2,
        TextureQuality.Maximum => 3,
        _ => 1,
    };
}
