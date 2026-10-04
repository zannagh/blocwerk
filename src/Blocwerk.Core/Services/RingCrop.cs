// <copyright file="RingCrop.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using SkiaSharp;

namespace Blocwerk.Core.Services;

/// <summary>
/// A review crop: a square around a point of an image (RAW pixels), with a ring of the hold's radius, scaled to
/// <see cref="SidePx"/>, as JPEG. Parts outside the image are black. Shared by the hold proposals and the confirm screen.
/// </summary>
public static class RingCrop
{
    /// <summary>The crop's side, px.</summary>
    public const int SidePx = 360;

    /// <summary>The crop shows at least this many source pixels around the point (or three radii).</summary>
    public const double MinHalfPx = 120;

    /// <summary>The crop, or null when the image does not decode.</summary>
    /// <param name="image">The encoded image.</param>
    /// <param name="x">Centre x, px.</param>
    /// <param name="y">Centre y, px.</param>
    /// <param name="radius">The ring's radius, px.</param>
    /// <returns>JPEG bytes, or null.</returns>
    public static byte[]? Render(byte[] image, double x, double y, double radius)
    {
        var half = Math.Max(MinHalfPx, 3 * radius);
        using var bitmap = SKBitmap.Decode(image);
        if (bitmap is null)
        {
            return null;
        }

        using var surface = SKSurface.Create(new SKImageInfo(SidePx, SidePx));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        var src = new SKRect((float)(x - half), (float)(y - half), (float)(x + half), (float)(y + half));
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(bitmap, src, new SKRect(0, 0, SidePx, SidePx), paint);
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = SKColors.Magenta };
        canvas.DrawCircle(SidePx / 2f, SidePx / 2f, (float)Math.Max(10, radius * SidePx / (2 * half)), ring);
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }
}
