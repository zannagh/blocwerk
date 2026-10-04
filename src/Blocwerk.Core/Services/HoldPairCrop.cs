// <copyright file="HoldPairCrop.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using SkiaSharp;

namespace Blocwerk.Core.Services;

/// <summary>
/// The review picture of a duplicate suggestion: a <see cref="RingCrop.SidePx"/> square of the panel photo around one hold, wide
/// enough to show both holds' whole outlines, with the focused hold's outline in yellow and the other's dashed in magenta.
/// The photo is turned upright by its EXIF tag first, because hold coordinates are relative to the upright photo.
/// </summary>
public static class HoldPairCrop
{
    private const double Margin = 1.3;

    /// <summary>The crop, or null when the photo does not decode.</summary>
    /// <param name="photo">The encoded panel photo.</param>
    /// <param name="focus">The hold the crop is centred on.</param>
    /// <param name="other">The other hold of the pair.</param>
    /// <returns>JPEG bytes, or null.</returns>
    public static byte[]? Render(byte[] photo, Hold focus, Hold other)
    {
        SKBitmap? decoded;
        try
        {
            decoded = SKBitmap.Decode(photo);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }

        if (decoded is null)
        {
            return null;
        }

        using (decoded)
        {
            using var upright = ImageRendition.Reorient(decoded, ImageRendition.OriginOf(photo));
            return Draw(upright ?? decoded, focus, other);
        }
    }

    private static byte[] Draw(SKBitmap bitmap, Hold focus, Hold other)
    {
        int w = bitmap.Width;
        int h = bitmap.Height;
        var mine = Pixels(focus, w, h);
        var theirs = Pixels(other, w, h);
        double cx = focus.X * w;
        double cy = focus.Y * h;
        double reach = mine.Concat(theirs).Max(p => Math.Max(Math.Abs(p.X - cx), Math.Abs(p.Y - cy)));
        double half = Math.Max(RingCrop.MinHalfPx, reach * Margin);
        float scale = (float)(RingCrop.SidePx / (2 * half));

        using var surface = SKSurface.Create(new SKImageInfo(RingCrop.SidePx, RingCrop.SidePx));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        var src = new SKRect((float)(cx - half), (float)(cy - half), (float)(cx + half), (float)(cy + half));
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(bitmap, src, new SKRect(0, 0, RingCrop.SidePx, RingCrop.SidePx), paint);
        Outline(canvas, theirs, cx, cy, half, scale, SKColors.Magenta, 2, dashed: true);
        Outline(canvas, mine, cx, cy, half, scale, SKColors.Yellow, 3, dashed: false);
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private static P2[] Pixels(Hold hold, int w, int h) =>
        HoldShapePolygon.Of(hold).Select(p => new P2(p.X * w, p.Y * h)).ToArray();

    private static void Outline(SKCanvas canvas, P2[] poly, double cx, double cy, double half, float scale, SKColor color, float width, bool dashed)
    {
        using var path = new SKPath();
        for (int i = 0; i < poly.Length; i++)
        {
            var x = (float)((poly[i].X - cx + half) * scale);
            var y = (float)((poly[i].Y - cy + half) * scale);
            if (i == 0)
            {
                path.MoveTo(x, y);
            }
            else
            {
                path.LineTo(x, y);
            }
        }

        path.Close();
        using var effect = dashed ? SKPathEffect.CreateDash([8, 5], 0) : null;
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, Color = color, PathEffect = effect };
        canvas.DrawPath(path, stroke);
    }
}
