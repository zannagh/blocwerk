using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Image fixtures for the variant tests. Noise rather than a flat fill so a JPEG of it is
/// genuinely large and downscaling it genuinely saves bytes — a solid colour compresses to almost
/// nothing and would make every size assertion meaningless.
/// </summary>
public static class TestImages
{
    /// <summary>A photo-like image of the given size, encoded in the given format.</summary>
    public static byte[] Noise(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg, int quality = 95)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var random = new Random(7);
        var pixels = bitmap.Pixels;
        byte r = 120, g = 100, b = 90;
        for (var i = 0; i < pixels.Length; i++)
        {
            r = (byte)Math.Clamp(r + random.Next(-9, 10), 0, 255);
            g = (byte)Math.Clamp(g + random.Next(-9, 10), 0, 255);
            b = (byte)Math.Clamp(b + random.Next(-9, 10), 0, 255);
            pixels[i] = new SKColor(r, g, b);
        }

        bitmap.Pixels = pixels;

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>
    /// A half-transparent PNG, standing in for what the image stitcher uploads: it composes onto a
    /// canvas that is never background-filled, so its uploads carry real alpha.
    /// </summary>
    public static byte[] TransparentPng(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new SKColor(220, 60, 60, 255) };
            canvas.DrawRect(0, 0, width / 2f, height, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// <paramref name="jpeg"/> with an EXIF APP1 segment declaring the given orientation — what a
    /// phone held sideways actually uploads. Skia cannot write EXIF, so the segment is assembled by
    /// hand and spliced in directly after the SOI marker, which is where a real encoder puts it.
    /// </summary>
    public static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        // Big-endian TIFF header, then one IFD entry: tag 0x0112 (Orientation), type 3 (SHORT),
        // count 1. A SHORT occupies the FIRST two bytes of the four-byte value field big-endian,
        // so the orientation is written there and the remaining two are padding.
        byte[] tiff =
        [
            0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01,
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01,
            (byte)(orientation >> 8), (byte)(orientation & 0xFF), 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];

        byte[] header = [0x45, 0x78, 0x69, 0x66, 0x00, 0x00]; // "Exif\0\0"
        var payloadLength = header.Length + tiff.Length + 2;  // the length field counts itself

        var app1 = new List<byte> { 0xFF, 0xE1, (byte)(payloadLength >> 8), (byte)(payloadLength & 0xFF) };
        app1.AddRange(header);
        app1.AddRange(tiff);

        // Straight after SOI (FF D8).
        var result = new List<byte>(jpeg.Length + app1.Count);
        result.AddRange(jpeg.Take(2));
        result.AddRange(app1);
        result.AddRange(jpeg.Skip(2));
        return [.. result];
    }
}
