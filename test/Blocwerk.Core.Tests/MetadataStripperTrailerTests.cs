using Blocwerk.Core.Capture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Phones append whole second images and videos after the primary JPEG's EOI — iPhone HDR gain maps
/// and depth maps (Multi-Picture Format, each with its OWN EXIF, GPS included) and Motion Photo MP4s
/// with a location atom. The stripper must end the file at the primary image's EOI and still keep
/// the compressed pixel data byte for byte (marker corners were detected on it).
/// </summary>
public class MetadataStripperTrailerTests
{
    [Fact]
    public void AppendedSecondJpeg_WithGpsExif_IsDropped()
    {
        var primary = CaptureScenario.TinyJpeg();
        byte[] withGainMap = [.. ExifJpeg.Build(primary), .. ExifJpeg.Build(CaptureScenario.TinyJpeg(seed: 3))];

        var stripped = ImageMetadataStripper.Strip(withGainMap);

        Assert.Equal(primary, stripped);
        AssertNoMetadata(stripped);
        Assert.Equal(HostileImages.Pixels(primary), HostileImages.Pixels(stripped));
    }

    [Fact]
    public void MotionPhotoVideo_AfterTheImage_IsDropped()
    {
        var primary = CaptureScenario.TinyJpeg();
        byte[] motionPhoto = [.. ExifJpeg.Build(primary), .. HostileImages.MotionPhotoTrailer];

        var stripped = ImageMetadataStripper.Strip(motionPhoto);

        Assert.Equal(primary, stripped);
        Assert.Equal(-1, stripped.AsSpan().IndexOf("ftyp"u8));
        Assert.Equal(-1, stripped.AsSpan().IndexOf("©xyz"u8));
    }

    [Fact]
    public void MpfSegment_IsDropped_WithTheImagesItPointsAt()
    {
        var primary = CaptureScenario.TinyJpeg();
        byte[] mpf = [.. primary[..2], .. HostileImages.MpfSegment(), .. primary[2..], .. ExifJpeg.Build(CaptureScenario.TinyJpeg(seed: 4))];

        var stripped = ImageMetadataStripper.Strip(mpf);

        Assert.Equal(primary, stripped);
        Assert.Equal(-1, stripped.AsSpan().IndexOf("MPF\0"u8));
    }

    [Fact]
    public void ProgressiveJpeg_KeepsEveryScan_AndDecodesToTheSamePixels()
    {
        var original = HostileImages.ProgressiveJpeg;
        byte[] withMetadata = [.. ExifJpeg.Build(original), .. HostileImages.MotionPhotoTrailer];

        var stripped = ImageMetadataStripper.Strip(withMetadata);

        Assert.Equal(original, stripped);
        Assert.Equal(10, CountSos(stripped));
        AssertNoMetadata(stripped);
        Assert.Equal(HostileImages.Pixels(original), HostileImages.Pixels(stripped));
    }

    [Fact]
    public void PngTrailer_AfterIend_IsDropped()
    {
        var png = HostileImages.PngClaiming(32, 32);
        byte[] withTrailer = [.. png, .. ExifJpeg.Build(CaptureScenario.TinyJpeg())];

        Assert.Equal(png, ImageMetadataStripper.Strip(withTrailer));
    }

    [Fact]
    public async Task Upload_StoresOnlyThePrimaryImage()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        var primary = CaptureScenario.TinyJpeg();
        byte[] upload = [.. ExifJpeg.Build(primary), .. ExifJpeg.Build(CaptureScenario.TinyJpeg(seed: 2)), .. HostileImages.MotionPhotoTrailer];

        await s.Service.AddPhotoAsync(draft.CaptureId, "IMG_1.jpg", upload, CancellationToken.None);

        var stored = Directory.GetFiles(Path.GetDirectoryName(s.Files.ResolvePhysicalPath("x.jpg"))!, "*.jpg").Single();
        Assert.Equal(primary, await File.ReadAllBytesAsync(stored));
    }

    private static void AssertNoMetadata(byte[] bytes)
    {
        Assert.Equal(-1, bytes.AsSpan().IndexOf("Exif\0\0"u8));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(ExifJpeg.GpsLatitudeBytes));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("GPSLatitude"u8));
    }

    private static int CountSos(byte[] jpeg) =>
        Enumerable.Range(0, jpeg.Length - 1).Count(i => jpeg[i] == 0xFF && jpeg[i + 1] == 0xDA);
}
