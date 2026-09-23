using System.Buffers.Binary;
using Blocwerk.Core.Capture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The EXIF reader and the metadata stripper run on untrusted upload bytes BEFORE anything else has
/// looked at them. Every hostile length — negative when cast, zero, past the end, truncated — must end
/// quickly in "no EXIF" / <see cref="InvalidDataException"/>, never in a spin (a PNG chunk length of
/// 0xFFFFFFF4 used to cast to −12 and loop forever inside the upload request).
/// </summary>
public class CaptureParserHardeningTests
{
    [Theory]
    [InlineData(0xFFFFFFF4u)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x80000000u)]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(12u)]
    [InlineData(0u)]
    public async Task PngChunkLength_ThatWouldNeverAdvance_EndsInNoExif(uint length)
    {
        var png = HostileImages.PngWithRawChunk(length, "tEXt", payloadBytes: 4);

        var exif = await HostileImages.Bounded(() => ExifCameraReader.Read(png));

        Assert.Same(ExifCameraInfo.Empty, exif);
        await HostileImages.Bounded(() => Record.Exception(() => ImageMetadataStripper.Strip(png)));
    }

    [Fact]
    public async Task PngEXifChunk_LongerThanTheFile_IsNoExif()
    {
        var png = HostileImages.PngWithRawChunk(10_000, "eXIf", payloadBytes: 16);

        Assert.Same(ExifCameraInfo.Empty, await HostileImages.Bounded(() => ExifCameraReader.Read(png)));
    }

    [Fact]
    public async Task ManyZeroLengthPngChunks_Terminate()
    {
        var chunks = Enumerable.Range(0, 5_000).SelectMany(_ => HostileImages.Chunk("zZzZ", [])).ToArray();
        byte[] png = [.. HostileImages.PngClaiming(32, 32)[..33], .. chunks, .. HostileImages.Chunk("IEND", [])];

        Assert.Same(ExifCameraInfo.Empty, await HostileImages.Bounded(() => ExifCameraReader.Read(png)));
        Assert.NotEmpty(await HostileImages.Bounded(() => ImageMetadataStripper.Strip(png)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(0xFFFF)]
    public async Task JpegSegmentLength_OutOfRange_IsRefused_NotLooped(int length)
    {
        var jpeg = CaptureScenario.TinyJpeg();
        var bad = new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length, (byte)'E', (byte)'x' };
        byte[] hostile = [.. jpeg[..2], .. bad, .. jpeg[2..]];

        Assert.Same(ExifCameraInfo.Empty, await HostileImages.Bounded(() => ExifCameraReader.Read(hostile)));
        var error = await HostileImages.Bounded(() => Record.Exception(() => ImageMetadataStripper.Strip(hostile)));
        Assert.IsType<InvalidDataException>(error);
    }

    [Fact]
    public async Task TruncatedJpeg_AtEveryLength_EndsCleanly()
    {
        var jpeg = ExifJpeg.Build(CaptureScenario.TinyJpeg());
        for (var cut = 3; cut < jpeg.Length - 2; cut += 7)
        {
            var truncated = jpeg[..cut];
            await HostileImages.Bounded(() => ExifCameraReader.Read(truncated));
            var error = await HostileImages.Bounded(() => Record.Exception(() => ImageMetadataStripper.Strip(truncated)));
            Assert.IsType<InvalidDataException>(error);
        }
    }

    [Fact]
    public async Task RandomBytes_BehindJpegAndPngMagic_AlwaysTerminate()
    {
        var random = new Random(20260923);
        for (var i = 0; i < 2_000; i++)
        {
            var body = new byte[random.Next(0, 600)];
            random.NextBytes(body);
            if (i % 3 == 0)
            {
                // Plant marker-looking bytes so the walk goes deep instead of failing at once.
                for (var k = 0; k + 1 < body.Length; k += random.Next(2, 40))
                {
                    body[k] = 0xFF;
                }
            }

            byte[] image = i % 2 == 0 ? [0xFF, 0xD8, 0xFF, .. body] : [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. body];
            await HostileImages.Bounded(() => ExifCameraReader.Read(image));
            var error = await HostileImages.Bounded(() => Record.Exception(() => ImageMetadataStripper.Strip(image)));
            Assert.True(error is null or InvalidDataException, error?.ToString());
        }
    }

    [Fact]
    public async Task Upload_OfAHostilePng_FailsFast_WithAFriendlyMessage()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        var png = HostileImages.PngWithRawChunk(0xFFFFFFF4u, "tEXt", payloadBytes: 4);

        var upload = s.Service.AddPhotoAsync(draft.CaptureId, "evil.png", png, CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => upload.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("evil.png", error.Message);
    }

    [Fact]
    public void ExifIsStillRead_FromAValidJpeg()
    {
        var exif = ExifCameraReader.Read(ExifJpeg.Build(CaptureScenario.TinyJpeg()));

        Assert.Equal(("Apple", "iPhone 16 Pro", 14.0), (exif.Make, exif.Model, exif.Focal35mm));
    }

    [Fact]
    public void ExifIsStillRead_FromAPngEXifChunk()
    {
        var jpegExif = ExifJpeg.Build(CaptureScenario.TinyJpeg());
        var app1 = jpegExif.AsSpan(2);
        var tiff = app1.Slice(10, BinaryPrimitives.ReadUInt16BigEndian(app1[2..]) - 8).ToArray();
        byte[] png = [.. HostileImages.PngClaiming(32, 32)[..33], .. HostileImages.Chunk("eXIf", tiff), .. HostileImages.Chunk("IEND", [])];

        Assert.Equal(14.0, ExifCameraReader.Read(png).Focal35mm);
    }
}
