// <copyright file="HybridZoomOptions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.HybridZoom;

namespace Blocwerk.Core.Services.HybridZoom;

/// <summary>
/// The "hybrid zoom" experiment: a zoomed-in panel view swaps the panel photo for a sharper close-up from the 3D
/// capture's photos, warped onto the panel so the hold overlay stays on it. Off by default. Bound from the
/// <c>HybridZoom</c> configuration section (env <c>HybridZoom__Enabled=true</c>).
/// </summary>
public sealed class HybridZoomOptions
{
    /// <summary>The configuration section name.</summary>
    public const string Section = "HybridZoom";

    /// <summary>Whether the close-up endpoint answers and the panel viewer asks it. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Zoom level (1 = fit) from which the panel viewer asks for a close-up.</summary>
    public double MinZoom { get; set; } = 2.5;

    /// <summary>Least resolution gain over the panel photo for a close-up to be shown at all.</summary>
    public double MinGain { get; set; } = 1.3;

    /// <summary>Least cosine between the facet normal and the direction to the close-up camera (0.5 = 60°).</summary>
    public double MinFrontality { get; set; } = 0.5;

    /// <summary>Longest side of a rendered close-up, px.</summary>
    public int MaxOutputPx { get; set; } = 2048;

    /// <summary>JPEG quality of the rendered close-up.</summary>
    public int JpegQuality { get; set; } = 82;

    /// <summary>Renders that may run at once (each decodes a full capture photo); further requests are told "busy".</summary>
    public int MaxConcurrentRenders { get; set; } = 2;

    /// <summary>
    /// A view with at least this share on one volume gets no close-up (the 3D view's job); volumes covering less of it
    /// are cut out of the close-up instead, so the panel photo shows there.
    /// </summary>
    public double VolumeShare { get; set; } = 0.5;

    /// <summary>
    /// How long after the capture's last photo upload a panel photo may have been added and still get close-ups: later
    /// than that the wall may have been reset since, and the capture would show holds the panel overlay does not have.
    /// Generous by default because a capture and the panel update are often uploaded on consecutive days. 0 = no check.
    /// </summary>
    public double CaptureFreshnessHours { get; set; } = 36;

    /// <summary>Match the close-up's colours to the panel photo before caching (<see cref="Blocwerk.Core.Geometry.HybridZoom.ColorMatch"/>). Default true.</summary>
    public bool ColorMatch { get; set; } = true;

    /// <summary>Rendered close-ups kept in memory.</summary>
    public int CacheEntries { get; set; } = 96;

    /// <summary>The selector's thresholds.</summary>
    /// <returns>The rules.</returns>
    public CloseUpRules Rules() => new(MinGain: MinGain, MinFrontality: MinFrontality);

    /// <summary>The freshness window, or null when it is not checked.</summary>
    /// <returns>The window.</returns>
    public TimeSpan? CaptureFreshness() => CaptureFreshnessHours > 0 ? TimeSpan.FromHours(CaptureFreshnessHours) : null;
}
