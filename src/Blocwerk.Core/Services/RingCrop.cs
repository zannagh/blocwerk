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

    /// <summary>Half the side of the square a crop around a ring of this radius shows, px.</summary>
    /// <param name="radius">The ring's radius, px.</param>
    /// <returns>Three radii, at least <see cref="MinHalfPx"/>.</returns>
    public static double HalfFor(double radius) => Math.Max(MinHalfPx, 3 * radius);

    /// <summary>The crop, or null when the image does not decode.</summary>
    /// <param name="image">The encoded image.</param>
    /// <param name="x">Centre x, px.</param>
    /// <param name="y">Centre y, px.</param>
    /// <param name="radius">The ring's radius, px.</param>
    /// <param name="halfPx">
    /// Half the side of the square of source pixels the crop shows, when the caller knows it (two pictures that have
    /// to show the same piece of wall); null takes <see cref="HalfFor"/>.
    /// </param>
    /// <param name="ring">False leaves the ring off: the caller draws the hold's real outline over the picture.</param>
    /// <returns>JPEG bytes, or null.</returns>
    public static byte[]? Render(byte[] image, double x, double y, double radius, double? halfPx = null, bool ring = true)
    {
        var half = halfPx ?? HalfFor(radius);
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
        if (ring)
        {
            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = SKColors.Magenta };
            canvas.DrawCircle(SidePx / 2f, SidePx / 2f, (float)Math.Max(10, radius * SidePx / (2 * half)), stroke);
        }

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }
}
