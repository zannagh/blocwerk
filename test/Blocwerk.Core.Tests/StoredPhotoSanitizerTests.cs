using System.Buffers.Binary;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Services;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Wall, panel and gallery photos lose their location and every other piece of metadata at ingest, but —
/// unlike capture photos — keep the EXIF orientation: holds are stored in the raw pixel grid and browsers
/// rotate by that tag, so dropping it would turn the photo underneath its holds.
/// </summary>
public class StoredPhotoSanitizerTests
{
    [Fact]
    public void Jpeg_LosesGpsAndCameraMetadata_KeepsOrientation6_AndImageDataByteForByte()
    {
        var original = PrivacyPhotos.GpsJpeg();
        Assert.Equal(new PhotoMetadataFacts(HasLocation: true, NeedsCleaning: true), StoredPhotoSanitizer.Inspect(original));

        var clean = StoredPhotoSanitizer.Sanitize(original);

        PrivacyPhotos.AssertCleanJpeg(original, clean);
        Assert.Equal(-1, clean.AsSpan().IndexOf("http://ns.adobe.com/xap"u8));
        Assert.Equal(HostileImages.Pixels(original), HostileImages.Pixels(clean));
        Assert.Equal(default, StoredPhotoSanitizer.Inspect(clean));
    }

    [Fact]
    public void Sanitize_IsIdempotent()
    {
        var once = StoredPhotoSanitizer.Sanitize(PrivacyPhotos.GpsJpeg());
        Assert.Equal(once, StoredPhotoSanitizer.Sanitize(once));

        var png = StoredPhotoSanitizer.Sanitize(PrivacyPhotos.GpsPng());
        Assert.Equal(png, StoredPhotoSanitizer.Sanitize(png));
    }

    [Fact]
    public void Png_WithAnExifChunk_LosesGps_KeepsOrientation_AndIdat()
    {
        var original = PrivacyPhotos.GpsPng();
        Assert.True(StoredPhotoSanitizer.Inspect(original).HasLocation);

        var clean = StoredPhotoSanitizer.Sanitize(original);

        PrivacyPhotos.AssertNoLocation(clean);

        // Skia does not read a PNG's eXIf orientation (browsers may), so the chunk is checked directly:
        // exactly the one-entry Orientation=6 TIFF a phone's JPEG would carry.
        var exif = clean.AsSpan().IndexOf("eXIf"u8);
        var orientationOnly = TestImages.WithExifOrientation(TestImages.Noise(8, 8), 6).AsSpan(12, 26).ToArray();
        Assert.Equal(orientationOnly, clean.AsSpan(exif + 4, 26).ToArray());
        Assert.Equal(PrivacyPhotos.PngImageData(original), PrivacyPhotos.PngImageData(clean));
        Assert.Equal(HostileImages.Pixels(original), HostileImages.Pixels(clean));
    }

    [Fact]
    public void PhotoWithoutOrientation_GetsNoExifAtAll()
    {
        var jpeg = TestImages.Noise(40, 30);

        var clean = StoredPhotoSanitizer.Sanitize(jpeg);

        Assert.Equal(-1, clean.AsSpan().IndexOf("Exif\0\0"u8));
        Assert.Equal(PrivacyPhotos.JpegImageData(jpeg), PrivacyPhotos.JpegImageData(clean));
    }

    [Fact]
    public void OrientationOnlyPhoto_IsReportedClean()
    {
        var jpeg = TestImages.WithExifOrientation(TestImages.Noise(40, 30), 6);

        Assert.Equal(default, StoredPhotoSanitizer.Inspect(jpeg));
        Assert.Equal(SKEncodedOrigin.RightTop, PrivacyPhotos.Orientation(StoredPhotoSanitizer.Sanitize(jpeg)));
    }

    [Fact]
    public void IccProfile_Survives()
    {
        var jpeg = TestImages.Noise(40, 30);
        byte[] icc = [.. "ICC_PROFILE\0"u8, 1, 1, .. new byte[64]];
        var segment = new byte[4 + icc.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE2;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(icc.Length + 2));
        icc.CopyTo(segment, 4);
        byte[] withProfile = [.. jpeg.AsSpan(0, 2), .. segment, .. ExifJpeg.Build(jpeg).AsSpan(2)];

        var clean = StoredPhotoSanitizer.Sanitize(withProfile);

        Assert.NotEqual(-1, clean.AsSpan().IndexOf(segment));
        PrivacyPhotos.AssertNoLocation(clean);
    }

    [Fact]
    public void BrokenJpeg_IsRefusedReadably_OtherFormatsPassThrough()
    {
        var truncated = PrivacyPhotos.GpsJpeg()[..200];

        var ex = Assert.Throws<InvalidOperationException>(() => StoredPhotoSanitizer.Sanitize(truncated));
        Assert.Contains("damaged", ex.Message);
        Assert.Equal(default, StoredPhotoSanitizer.Inspect(truncated));

        byte[] placeholder = [1, 2, 3];
        Assert.Same(placeholder, StoredPhotoSanitizer.Sanitize(placeholder));
        var webp = TestImages.Noise(20, 20, SKEncodedImageFormat.Webp);
        Assert.Equal(webp, StoredPhotoSanitizer.Sanitize(webp)); // a metadata-free WebP is unchanged (see WebpMetadataStripperTests)
    }

    [Fact]
    public void Rendition_OfACleanedPhoto_IsUprightAndCarriesNoMetadata()
    {
        var clean = StoredPhotoSanitizer.Sanitize(ExifJpeg.Build(TestImages.Noise(800, 600)));

        var rendition = ImageRendition.Render(clean, 320);

        Assert.NotNull(rendition);
        Assert.Equal(-1, rendition.Bytes.AsSpan().IndexOf("Exif\0\0"u8));
        using var bitmap = SKBitmap.Decode(rendition.Bytes);
        Assert.Equal(320, bitmap.Width);
        Assert.Equal(427, bitmap.Height); // orientation 6 baked in: portrait
    }
}
