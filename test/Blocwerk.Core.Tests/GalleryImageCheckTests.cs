// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Buffers.Binary;
using Blocwerk.Core.Capture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A gallery upload is stored only as the format it was declared as (a GPS-tagged TIFF or a HEIC sent as image/jpeg
/// would otherwise pass the sanitizer unchanged), and only when its header names a picture under the pixel limit.
/// </summary>
public sealed class GalleryImageCheckTests
{
    [Fact]
    public void TheDeclaredFormat_Passes()
    {
        Assert.Null(GalleryImageCheck.Refusal(PrivacyPhotos.GpsJpeg(), "image/jpeg"));
        Assert.Null(GalleryImageCheck.Refusal(PrivacyPhotos.GpsPng(), "image/png"));
    }

    [Fact]
    public void AnotherFormat_UnderAnImageType_Is415()
    {
        Assert.Equal(415, GalleryImageCheck.Refusal(PrivacyPhotos.ExifTiff(), "image/jpeg")?.Status);
        Assert.Equal(415, GalleryImageCheck.Refusal(Heic(), "image/jpeg")?.Status);
        Assert.Equal(415, GalleryImageCheck.Refusal(PrivacyPhotos.GpsPng(), "image/jpeg")?.Status);
        Assert.Equal(415, GalleryImageCheck.Refusal(PrivacyPhotos.GpsJpeg(), "image/webp")?.Status);
    }

    [Fact]
    public void AHeaderBeyondThePixelLimit_Is400_WithoutDecoding()
    {
        Assert.Equal(400, GalleryImageCheck.Refusal(PngHeader(60_000, 60_000), "image/png")?.Status);
        Assert.Equal(400, GalleryImageCheck.Refusal([0xFF, 0xD8, 0xFF, 0x00], "image/jpeg")?.Status);
    }

    private static byte[] Heic() => [0, 0, 0, 0x18, .. "ftypheic"u8, 0, 0, 0, 0, .. "mif1heic"u8];

    // A PNG whose IHDR claims width x height, with no pixel data: the header alone is a decode bomb's promise.
    private static byte[] PngHeader(int width, int height)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        return [.. CapturePhotoFormat.PngSignature, .. PrivacyPhotos.Chunk("IHDR", ihdr), .. PrivacyPhotos.Chunk("IEND", [])];
    }
}
