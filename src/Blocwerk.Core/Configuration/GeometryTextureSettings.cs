// <copyright file="GeometryTextureSettings.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Configuration;

/// <summary>
/// The wall textures' <c>options</c> the app sends to the geometry worker's <c>textures</c> job
/// (<c>docker/wall-geometry/README.md</c>). Each is optional: an unset (or out-of-range) value is not sent,
/// so the worker's own default applies (2 mm/px, 4096 px, JPEG 90), and with none set the request carries
/// no <c>options</c> part at all, exactly as before. Bound from <c>Blocwerk:GeometryService:Textures:*</c>
/// or the <c>GEOMETRYSERVICE__TEXTURES__MMPERPX / __MAXSIDEPX / __JPEGQUALITY</c> environment variables.
/// </summary>
public class GeometryTextureSettings
{
    /// <summary>Worker-side bounds of <see cref="MmPerPx"/> (<c>CLIENT_OPTIONS</c> in textures.py).</summary>
    public const double MinMmPerPx = 0.25;

    public const double MaxMmPerPx = 50;

    /// <summary>
    /// Worker-side bounds of <see cref="MaxSidePx"/>; below the app's own
    /// <see cref="Capture.CaptureComputeDocuments.MaxTextureSidePx"/>, so every accepted value comes back readable.
    /// </summary>
    public const int MinSidePx = 256;

    public const int MaxSidePxLimit = 8192;

    /// <summary>Worker-side bounds of <see cref="JpegQuality"/>.</summary>
    public const int MinJpegQuality = 30;

    public const int MaxJpegQuality = 100;

    /// <summary>Texture resolution on the facet plane, mm per pixel (0.25–50); null = the worker's 2.0.</summary>
    public double? MmPerPx { get; set; }

    /// <summary>Longest texture side in pixels (256–8192); null = the worker's 4096.</summary>
    public int? MaxSidePx { get; set; }

    /// <summary>JPEG quality of the textures (30–100); null = the worker's 90.</summary>
    public int? JpegQuality { get; set; }

    /// <summary>Whether any option is set (and so an <c>options</c> part is sent).</summary>
    public bool IsConfigured => MmPerPx.HasValue || MaxSidePx.HasValue || JpegQuality.HasValue;

    /// <summary>Binds <c>Blocwerk:GeometryService:Textures:*</c> or <c>GEOMETRYSERVICE__TEXTURES__*</c>.</summary>
    public static GeometryTextureSettings Bind(IConfigurationSection section)
    {
        string? Read(string key) =>
            section[$"GeometryService:Textures:{key}"]
            ?? Environment.GetEnvironmentVariable($"GEOMETRYSERVICE__TEXTURES__{key.ToUpperInvariant()}");

        return new GeometryTextureSettings
        {
            MmPerPx = double.TryParse(Read("MmPerPx"), NumberStyles.Float, CultureInfo.InvariantCulture, out var mm)
                      && mm is >= MinMmPerPx and <= MaxMmPerPx
                ? mm
                : null,
            MaxSidePx = ReadInt(Read("MaxSidePx"), MinSidePx, MaxSidePxLimit),
            JpegQuality = ReadInt(Read("JpegQuality"), MinJpegQuality, MaxJpegQuality),
        };
    }

    /// <summary>The <c>options</c> JSON of a textures job, or null when nothing is set (no part is sent).</summary>
    public string? ToOptionsJson()
    {
        if (!IsConfigured)
        {
            return null;
        }

        var options = new JsonObject();
        if (MmPerPx is { } mm)
        {
            options["mmPerPx"] = mm;
        }

        if (MaxSidePx is { } side)
        {
            options["maxSidePx"] = side;
        }

        if (JpegQuality is { } quality)
        {
            options["jpegQuality"] = quality;
        }

        return options.ToJsonString();
    }

    private static int? ReadInt(string? raw, int min, int max) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : null;
}
