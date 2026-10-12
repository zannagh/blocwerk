// <copyright file="HoldColorTestPhoto.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using SkiaSharp;

namespace Blocwerk.Core.Tests.HoldColors;

/// <summary>A synthetic panel photo: a wooden wall with round, flat-coloured holds on it.</summary>
internal static class HoldColorTestPhoto
{
    public const int Width = 400;
    public const int Height = 300;

    public static readonly SKColor Wood = new(222, 196, 160);
    public static readonly SKColor Blue = new(40, 90, 200);
    public static readonly SKColor Red = new(200, 45, 50);
    public static readonly SKColor Green = new(50, 160, 70);
    public static readonly SKColor Yellow = new(232, 200, 20);

    /// <summary>The photo as a PNG; each disc is (x, y, radius) as fractions of the photo (radius of the long side).</summary>
    public static byte[] Build(params (double X, double Y, double Radius, SKColor Color)[] discs) =>
        Encode(SKEncodedImageFormat.Png, discs);

    /// <summary>The same photo as a JPEG, as a camera would deliver it.</summary>
    public static byte[] BuildJpeg(params (double X, double Y, double Radius, SKColor Color)[] discs) =>
        Encode(SKEncodedImageFormat.Jpeg, discs);

    private static byte[] Encode(SKEncodedImageFormat format, (double X, double Y, double Radius, SKColor Color)[] discs)
    {
        using var bitmap = new SKBitmap(Width, Height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(Wood);
            foreach (var (x, y, radius, color) in discs)
            {
                using var paint = new SKPaint { Color = color, IsAntialias = false, Style = SKPaintStyle.Fill };
                canvas.DrawCircle((float)(x * Width), (float)(y * Height), (float)(radius * Width), paint);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }
}
