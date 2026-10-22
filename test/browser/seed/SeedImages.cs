// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using SkiaSharp;

namespace Blocwerk.Browser.Seed;

/// <summary>Deterministic, real image bytes: the variant route decodes and re-encodes them, so they must be valid.</summary>
internal static class SeedImages
{
    public static byte[] Jpeg(int width, int height, int seed) => Encode(Gradient(width, height, seed), SKEncodedImageFormat.Jpeg, 80);

    public static byte[] Png(int width, int height) => Encode(Gradient(width, height, 3), SKEncodedImageFormat.Png, 100);

    public static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // A smooth gradient with a little noise: distinct per seed, and small once compressed.
    private static SKBitmap Gradient(int width, int height, int seed)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var random = new Random(seed);
        var pixels = new SKColor[width * height];
        var tint = (byte)(seed * 37 % 255);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var jitter = random.Next(-4, 5);
                pixels[(y * width) + x] = new SKColor(
                    (byte)Math.Clamp((x * 255 / width) + jitter, 0, 255),
                    (byte)Math.Clamp((y * 255 / height) + jitter, 0, 255),
                    tint);
            }
        }

        bitmap.Pixels = pixels;
        return bitmap;
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using (bitmap)
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(format, quality);
            return data.ToArray();
        }
    }
}
