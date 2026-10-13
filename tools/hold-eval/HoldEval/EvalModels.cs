// <copyright file="EvalModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;

namespace Blocwerk.HoldEval;

/// <summary>How the run was configured.</summary>
/// <param name="BoxSeed">True when the outliner gets the detector box, false for the plain radius circle.</param>
/// <param name="Markers">True when detections on printed marker sheets are dropped, as the app does on a marker wall.</param>
/// <param name="Segmenter">True when the segmentation model outlines the boxed detections (its files lie next to the detector model).</param>
/// <param name="Traced">True to also record every outline as traced, before the panel's clean-up (outlines twice: slower).</param>
/// <param name="TileSize">Detection window edge in pixels; 0 for the app's default.</param>
/// <param name="Confidence">Minimum detector confidence; 0 for the app's default.</param>
public sealed record EvalSettings(bool BoxSeed, bool Markers, bool Segmenter, bool Traced, int TileSize, double Confidence)
{
    /// <summary>Reads the settings from the command line options.</summary>
    /// <param name="options">The <c>--key=value</c> options.</param>
    /// <returns>The settings.</returns>
    public static EvalSettings From(IReadOnlyDictionary<string, string> options) => new(
        options.GetValueOrDefault("seed", "box") == "box",
        options.GetValueOrDefault("markers", "true") != "false",
        options.GetValueOrDefault("segmenter", "true") != "false",
        options.GetValueOrDefault("traced", "false") == "true",
        int.Parse(options.GetValueOrDefault("tile", "0"), System.Globalization.CultureInfo.InvariantCulture),
        double.Parse(options.GetValueOrDefault("conf", "0"), System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>One detection and what each stage made of it. Coordinates are fractions of the image.</summary>
public sealed record EvalHold
{
    /// <summary>Gets the position in the output, which is what <see cref="StackedOn"/> refers to.</summary>
    public int Index { get; init; }

    /// <summary>Gets the centre X.</summary>
    public double X { get; init; }

    /// <summary>Gets the centre Y.</summary>
    public double Y { get; init; }

    /// <summary>Gets the radius after the clean-up (a fraction of the image's longer side).</summary>
    public double Radius { get; init; }

    /// <summary>Gets the radius as detected.</summary>
    public double DetectedRadius { get; init; }

    /// <summary>Gets the detector box width as a fraction of the image width.</summary>
    public double? BoxWidth { get; init; }

    /// <summary>Gets the detector box height as a fraction of the image height.</summary>
    public double? BoxHeight { get; init; }

    /// <summary>Gets a value indicating whether the detector called it a volume.</summary>
    public bool Volume { get; init; }

    /// <summary>Gets the detector confidence.</summary>
    public double Confidence { get; init; }

    /// <summary>Gets how the outline came about: Contour, GrabCut or CircleFallback.</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>Gets the outliner's confidence.</summary>
    public double OutlineConfidence { get; init; }

    /// <summary>Gets the outline as traced, before the panel's clean-up; null for a circle.</summary>
    public List<double[]>? Traced { get; init; }

    /// <summary>Gets the final outline as offsets from the centre, the way the app stores it; null for a circle.</summary>
    public List<double[]>? ShapePoints { get; init; }

    /// <summary>Gets the interior holes of the final outline.</summary>
    public List<List<double[]>>? ShapeHoles { get; init; }

    /// <summary>Gets the colour the app reads off the photo for it.</summary>
    public string? Color { get; init; }

    /// <summary>Gets the index of the hold it sits on, when the stacking rule says so.</summary>
    public int? StackedOn { get; init; }

    /// <summary>Gets a value indicating whether the app would drop it as a printed marker sheet.</summary>
    public bool OnMarker { get; init; }
}

/// <summary>One photo's run.</summary>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="DetectMs">Detector time.</param>
/// <param name="OutlineMs">Outliner and clean-up time.</param>
/// <param name="Settings">The settings.</param>
/// <param name="Holds">The detections.</param>
public sealed record EvalRun(int Width, int Height, double DetectMs, double OutlineMs, EvalSettings Settings, List<EvalHold> Holds);

/// <summary>JSON conventions of the harness.</summary>
public static class EvalJson
{
    /// <summary>Gets camel-case, compact options.</summary>
    public static JsonSerializerOptions Options { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
