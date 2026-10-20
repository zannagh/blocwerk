// <copyright file="IHybridZoomService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.HybridZoom;

namespace Blocwerk.Core.Services.HybridZoom;

/// <summary>Why a close-up was or was not rendered.</summary>
public enum CloseUpStatus
{
    /// <summary>A close-up was rendered (or is unchanged: <see cref="CloseUpResult.Image"/> is then null).</summary>
    Rendered,

    /// <summary>The panel is not on the wall or has no photo.</summary>
    NotFound,

    /// <summary>The wall has no active 3D model with solved cameras and retained capture photos.</summary>
    NoModel,

    /// <summary>The panel photo was taken clearly after the capture: its photos show an older wall.</summary>
    StaleCapture,

    /// <summary>The panel photo does not map onto the model there (too few placed holds or markers).</summary>
    NoMapping,

    /// <summary>No capture photo shows the view better than the panel photo.</summary>
    NoBetterPhoto,

    /// <summary>The view is mostly on a volume (see <see cref="CloseUpResult.Volume"/>).</summary>
    OnVolume,

    /// <summary>Too many renders are running; try again later.</summary>
    Busy,
}

/// <summary>The outcome of a close-up request.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Rect">The panel rectangle the close-up covers (may be smaller than asked when no photo covered all of it).</param>
/// <param name="Image">The close-up, warped so it lies exactly over <see cref="Rect"/>; null unless rendered fresh.</param>
/// <param name="ETag">A validator for the render (null when nothing was rendered).</param>
/// <param name="PhotoName">The capture photo it came from.</param>
/// <param name="Gain">Its resolution gain over the panel photo.</param>
/// <param name="Volume">
/// The volume to look underneath in 3D: the one the view is on (<see cref="CloseUpStatus.OnVolume"/>), or one cut out of
/// the close-up.
/// </param>
/// <param name="ContentType">The image's media type: JPEG, or WebP with alpha when volumes are cut out of it.</param>
public sealed record CloseUpResult(
    CloseUpStatus Status,
    ViewRect? Rect = null,
    byte[]? Image = null,
    string? ETag = null,
    string? PhotoName = null,
    double? Gain = null,
    VolumeSpot? Volume = null,
    string ContentType = "image/jpeg")
{
    /// <summary>Mean ΔE (CIE76) between close-up and panel photo before and after the colour match; null when it did not run.</summary>
    public (double Before, double After)? DeltaE { get; init; }

    /// <summary>ΔE of the two's mean colours (the cast) before and after the colour match; null when it did not run.</summary>
    public (double Before, double After)? CastDeltaE { get; init; }
}

/// <summary>
/// Renders the close-up of a zoomed panel view from the wall's 3D capture photos (see <see cref="HybridZoomOptions"/>).
/// Callers gate wall access; the service only reads.
/// </summary>
public interface IHybridZoomService
{
    /// <summary>The close-up of <paramref name="rect"/> of the panel photo.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="panelId">The panel.</param>
    /// <param name="rect">The view, panel-normalised.</param>
    /// <param name="maxPx">The longest side the caller can show, px.</param>
    /// <param name="aspect">The view's displayed aspect (width ÷ height, px), or null to derive it from the photo.</param>
    /// <param name="ifNoneMatch">The caller's cached validator: an unchanged render is not redone.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result.</returns>
    Task<CloseUpResult> RenderAsync(
        Guid wallId, Guid panelId, ViewRect rect, int maxPx, double? aspect, string? ifNoneMatch, CancellationToken ct);
}
