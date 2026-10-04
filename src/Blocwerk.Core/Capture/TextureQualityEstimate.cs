// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Configuration;

namespace Blocwerk.Core.Capture;

/// <summary>How the worker would render at a quality: the multi-view blend in full, with fewer views, or single-photo.</summary>
public enum TextureBlendFit
{
    Full = 0,
    FewerViews = 1,

    /// <summary>Even two views do not fit: plain single-photo render, no exposure balance or seam blending.</summary>
    SingleView = 2,

    /// <summary>More output pixels than the worker accepts in one job.</summary>
    TooLarge = 3,
}

/// <summary>What a re-render at one quality would take for a wall: pixels, the blend's memory and how long.</summary>
public sealed record TextureQualityEstimate(
    TextureQuality Quality, double MmPerPx, double MegaPixels, double BlendGb, int BlendViews, TextureBlendFit Fit, double RelativeDuration)
{
    /// <summary>The worker's blend budget (<c>TEXTURES_BLEND_MAX_BYTES</c>, default; the 4 GiB prod worker).</summary>
    public const double BlendBudgetBytes = 2.0e9;

    /// <summary>The worker's total pixel limit per job (<c>TEXTURES_MAX_MEGAPIXELS</c>, default).</summary>
    public const double MaxPixels = 200e6;

    private const double ExtraMarginMm = 100;

    /// <summary>Per photo, the sparse views' memory (The Attic: 0.55 GB for 356 photos).</summary>
    private const double ViewBytesPerPhoto = 1.6e6;

    /// <summary>The estimates of every quality for the facets of <paramref name="geometryJson"/> and <paramref name="photoCount"/> photos.</summary>
    public static IReadOnlyList<TextureQualityEstimate> ForAll(string geometryJson, int photoCount, GeometryTextureSettings configured)
    {
        var extents = FacetExtents(geometryJson);
        return Enum.GetValues<TextureQuality>().Select(q => For(q, extents, photoCount, configured)).ToList();
    }

    public static TextureQualityEstimate For(
        TextureQuality quality, IReadOnlyList<(double A, double B)> extents, int photoCount, GeometryTextureSettings configured)
    {
        var (mm, side, views) = TextureQualityPresets.Resolve(quality, configured);
        var sizes = extents.Select(e => Pixels(e, mm, side)).ToList();
        var total = sizes.Sum(s => s.W * s.H);
        var biggest = sizes.Count == 0 ? 0 : sizes.Max(s => s.W * s.H);
        var fitViews = views;
        while (fitViews > 1 && BlendBytes(fitViews, total, biggest, photoCount) > BlendBudgetBytes)
        {
            fitViews--;
        }

        var fit = total > MaxPixels ? TextureBlendFit.TooLarge
            : fitViews <= 1 ? TextureBlendFit.SingleView
            : fitViews < views ? TextureBlendFit.FewerViews
            : TextureBlendFit.Full;
        return new TextureQualityEstimate(
            quality, mm, Math.Round(total / 1e6, 1), Math.Round(BlendBytes(fitViews, total, biggest, photoCount) / 1e9, 2), fitViews, fit,
            TextureQualityPresets.RelativeDuration(quality));
    }

    /// <summary>Mirror of <c>textures.blend_bytes</c>: sample slots, the biggest facet's source map and the photos' views.</summary>
    internal static double BlendBytes(int views, double totalPixels, double biggestPixels, int photoCount) =>
        ((views + 2) * 7 * totalPixels) + (4 * biggestPixels) + (ViewBytesPerPhoto * photoCount);

    /// <summary>Mirror of <c>textures._grid</c>: the facet's pixel grid at <paramref name="mm"/> (never beyond the longest side).</summary>
    internal static (double W, double H) Pixels((double A, double B) extent, double mm, int maxSide)
    {
        var a = extent.A + (2 * ExtraMarginMm);
        var b = extent.B + (2 * ExtraMarginMm);
        var res = Math.Max(mm, Math.Max(a, b) / maxSide);
        return (Math.Max(1, Math.Ceiling(a / res)), Math.Max(1, Math.Ceiling(b / res)));
    }

    /// <summary>The (width, height) in mm of every facet in a geometry document.</summary>
    internal static List<(double A, double B)> FacetExtents(string geometryJson)
    {
        var result = new List<(double, double)>();
        foreach (var segment in JsonNode.Parse(geometryJson)?["segments"]?.AsArray() ?? [])
        {
            foreach (var facet in segment?["facets"]?.AsArray() ?? [])
            {
                if (facet?["extentMm"] is { } e)
                {
                    result.Add(((double)e["aMax"]! - (double)e["aMin"]!, (double)e["bMax"]! - (double)e["bMin"]!));
                }
            }
        }

        return result;
    }
}
