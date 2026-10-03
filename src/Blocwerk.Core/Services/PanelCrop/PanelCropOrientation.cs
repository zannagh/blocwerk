// <copyright file="PanelCropOrientation.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using SkiaSharp;

namespace Blocwerk.Core.Services.PanelCrop;

/// <summary>
/// Converts a normalized crop rectangle between the DISPLAYED frame (the photo as the browser turns it by its EXIF
/// orientation, which is what the editor draws the holds and the crop box over) and the RAW pixel grid the crop is cut
/// from. The crop keeps the source's orientation tag, so cutting the raw rectangle and letting the browser turn it gives
/// exactly the displayed rectangle. Origin values are the EXIF orientation numbers 1..8.
/// </summary>
public static class PanelCropOrientation
{
    /// <summary>The raw-grid rectangle under a displayed-frame rectangle.</summary>
    /// <param name="displayed">The rectangle in the displayed frame.</param>
    /// <param name="origin">The photo's EXIF orientation.</param>
    /// <returns>The rectangle in the raw grid.</returns>
    public static PanelCropRect ToRaw(PanelCropRect displayed, SKEncodedOrigin origin) =>
        Bounds(displayed, p => DisplayToRaw(p, origin));

    /// <summary>The displayed-frame rectangle a raw-grid rectangle shows as.</summary>
    /// <param name="raw">The rectangle in the raw grid.</param>
    /// <param name="origin">The photo's EXIF orientation.</param>
    /// <returns>The rectangle in the displayed frame.</returns>
    public static PanelCropRect ToDisplayed(PanelCropRect raw, SKEncodedOrigin origin) =>
        Bounds(raw, p => RawToDisplay(p, origin));

    /// <summary>Whether the orientation swaps the two axes (the quarter turns and transposes).</summary>
    /// <param name="origin">The orientation.</param>
    /// <returns>True for 5..8.</returns>
    public static bool SwapsAxes(SKEncodedOrigin origin) => origin >= SKEncodedOrigin.LeftTop;

    private static (double X, double Y) RawToDisplay((double X, double Y) p, SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopRight => (1 - p.X, p.Y),
        SKEncodedOrigin.BottomRight => (1 - p.X, 1 - p.Y),
        SKEncodedOrigin.BottomLeft => (p.X, 1 - p.Y),
        SKEncodedOrigin.LeftTop => (p.Y, p.X),
        SKEncodedOrigin.RightTop => (1 - p.Y, p.X),
        SKEncodedOrigin.RightBottom => (1 - p.Y, 1 - p.X),
        SKEncodedOrigin.LeftBottom => (p.Y, 1 - p.X),
        _ => p,
    };

    private static (double X, double Y) DisplayToRaw((double X, double Y) p, SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopRight => (1 - p.X, p.Y),
        SKEncodedOrigin.BottomRight => (1 - p.X, 1 - p.Y),
        SKEncodedOrigin.BottomLeft => (p.X, 1 - p.Y),
        SKEncodedOrigin.LeftTop => (p.Y, p.X),
        SKEncodedOrigin.RightTop => (p.Y, 1 - p.X),
        SKEncodedOrigin.RightBottom => (1 - p.Y, 1 - p.X),
        SKEncodedOrigin.LeftBottom => (1 - p.Y, p.X),
        _ => p,
    };

    /// <summary>The axis-aligned bounds of a rectangle's two opposite corners after mapping.</summary>
    private static PanelCropRect Bounds(PanelCropRect rect, Func<(double X, double Y), (double X, double Y)> map)
    {
        var a = map((rect.Left, rect.Top));
        var b = map((rect.Right, rect.Bottom));
        var (left, top) = (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
        return new PanelCropRect(left, top, Math.Max(a.X, b.X) - left, Math.Max(a.Y, b.Y) - top);
    }
}
