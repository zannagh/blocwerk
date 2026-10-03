using System.Buffers.Binary;
using Blocwerk.Core.Capture;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Fixtures for the photo-privacy tests: a phone-like JPEG and PNG carrying GPS and Orientation 6, and
/// the checks every ingest path is held to — no location left, orientation 6 kept, pixels byte for byte.
/// </summary>
internal static class PrivacyPhotos
{
    /// <summary>A JPEG with EXIF (make, model, Orientation 6, GPS latitude), XMP and a comment.</summary>
    public static byte[] GpsJpeg() => ExifJpeg.Build(TestImages.Noise(64, 48));

    /// <summary>A PNG with an eXIf chunk carrying the same TIFF (Orientation 6 and a GPS block) and a tEXt chunk.</summary>
    public static byte[] GpsPng()
    {
        var png = TestImages.Noise(64, 48, SKEncodedImageFormat.Png);
        var at = 8 + 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8));
        return [.. png.AsSpan(0, at), .. Chunk("eXIf", ExifTiff()), .. Chunk("tEXt", "Comment\0shot at home"u8.ToArray()), .. png.AsSpan(at)];
    }

    /// <summary>The TIFF inside <see cref="ExifJpeg.Build"/>'s EXIF APP1 (it sits right after SOI).</summary>
    public static byte[] ExifTiff()
    {
        var jpeg = ExifJpeg.Build(CaptureScenario.TinyJpeg());
        var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(4));
        return jpeg.AsSpan(4 + 2 + 6, length - 2 - 6).ToArray();
    }

    /// <summary>No GPS rationals, no XMP location, no camera identity, no comment.</summary>
    public static void AssertNoLocation(byte[] bytes)
    {
        Assert.Equal(-1, bytes.AsSpan().IndexOf(ExifJpeg.GpsLatitudeBytes));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("GPSLatitude"u8));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("iPhone"u8));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("shot at home"u8));
        Assert.False(StoredPhotoSanitizer.Inspect(bytes).HasLocation);
    }

    /// <summary>The orientation a decoder (Skia here, a browser in production) reads off the file.</summary>
    public static SKEncodedOrigin Orientation(byte[] bytes)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes));
        return codec.EncodedOrigin;
    }

    /// <summary>Everything of a JPEG except its APPn and COM segments: tables, frame header, every scan's data.</summary>
    public static byte[] JpegImageData(byte[] jpeg) =>
    [
        .. JpegStructure.Parse(jpeg)
            .Where(p => p.Marker is not ((>= 0xE0 and <= 0xEF) or 0xFE))
            .SelectMany(p => jpeg.AsSpan(p.Start, p.Length).ToArray()),
    ];

    /// <summary>The concatenated IDAT chunks of a PNG.</summary>
    public static byte[] PngImageData(byte[] png)
    {
        var data = new List<byte>();
        for (var pos = 8; pos + 12 <= png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            if (png.AsSpan(pos + 4, 4).SequenceEqual("IDAT"u8))
            {
                data.AddRange(png.AsSpan(pos, 12 + length).ToArray());
            }

            pos += 12 + length;
        }

        return [.. data];
    }

    /// <summary>The JPEG checks in one call: clean, still orientation 6, identical image data.</summary>
    public static void AssertCleanJpeg(byte[] original, byte[] stored)
    {
        AssertNoLocation(stored);
        Assert.Equal(SKEncodedOrigin.RightTop, Orientation(stored));
        Assert.Equal(JpegImageData(original), JpegImageData(stored));
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
