// <copyright file="WebpMetadataStripperTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Buffers.Binary;
using Blocwerk.Core.Capture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// WebP gallery uploads lose EXIF (GPS, device), XMP and unknown chunks; ICCP survives for stored
/// photos; the VP8X flags and the RIFF size always match what is left; the image chunk is byte for byte.
/// </summary>
public class WebpMetadataStripperTests
{
    [Fact]
    public void Sanitize_DropsLocation_KeepsIccAndOrientation_FixesFlagsAndSize()
    {
        var original = WebpFixtures.PhoneWebp();
        Assert.Equal(new PhotoMetadataFacts(HasLocation: true, NeedsCleaning: true), StoredPhotoSanitizer.Inspect(original));

        var clean = StoredPhotoSanitizer.Sanitize(original);

        PrivacyPhotos.AssertNoLocation(clean);
        Assert.Equal(-1, clean.AsSpan().IndexOf("trailing junk"u8));
        var chunks = WebpFixtures.Chunks(clean);
        Assert.Equal(["VP8X", "ICCP", "VP8 ", "EXIF"], chunks.Select(c => c.FourCc));
        Assert.Equal(WebpFixtures.Icc, chunks[1].Payload);
        Assert.Equal(WebpFixtures.IccFlag | WebpFixtures.ExifFlag, chunks[0].Payload[0]);
        Assert.Equal((uint)(clean.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(clean.AsSpan(4)));
        Assert.Equal(WebpFixtures.Chunks(original)[2].Payload, chunks[2].Payload);
        Assert.Equal(HostileImages.Pixels(original), HostileImages.Pixels(clean));
        Assert.Equal(6, ExifCameraReaderOrientation(clean));
        Assert.Equal(default, StoredPhotoSanitizer.Inspect(clean));
    }

    [Fact]
    public void Sanitize_IsIdempotent()
    {
        var once = StoredPhotoSanitizer.Sanitize(WebpFixtures.PhoneWebp());

        Assert.Equal(once, StoredPhotoSanitizer.Sanitize(once));
    }

    [Fact]
    public void WithoutIccOrOrientation_EveryMetadataFlagIsCleared()
    {
        var original = WebpFixtures.PhoneWebp(withIcc: false, exif: [.. "MM\0*"u8, 0, 0, 0, 8, 0, 0]);

        var clean = StoredPhotoSanitizer.Sanitize(original);

        var chunks = WebpFixtures.Chunks(clean);
        Assert.Equal(["VP8X", "VP8 "], chunks.Select(c => c.FourCc));
        Assert.Equal(0, chunks[0].Payload[0]);
        Assert.Equal((uint)(clean.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(clean.AsSpan(4)));
    }

    [Fact]
    public void CaptureStrip_DropsTheIccProfileToo()
    {
        var clean = ImageMetadataStripper.Strip(WebpFixtures.PhoneWebp());

        var chunks = WebpFixtures.Chunks(clean);
        Assert.Equal(["VP8X", "VP8 "], chunks.Select(c => c.FourCc));
        Assert.Equal(0, chunks[0].Payload[0]);
    }

    [Fact]
    public void SimpleWebp_ComesBackByteForByte()
    {
        var simple = WebpFixtures.Simple();

        Assert.Equal(simple, StoredPhotoSanitizer.Sanitize(simple));
        Assert.Equal(default, StoredPhotoSanitizer.Inspect(simple));
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0xFFFFFFF8u)]
    [InlineData(0x80000000u)]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(10_000u)]
    public async Task HostileChunkLength_IsRefused_NotLooped(uint size)
    {
        var webp = WebpFixtures.PhoneWebp();
        BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(12 + 4), size);

        var error = await HostileImages.Bounded(() => Record.Exception(() => ImageMetadataStripper.Strip(webp)));

        Assert.IsType<InvalidDataException>(error);
        Assert.Same(ExifCameraInfo.Empty, await HostileImages.Bounded(() => ExifCameraReader.Read(webp)));
        Assert.Throws<InvalidOperationException>(() => StoredPhotoSanitizer.Sanitize(webp));
    }

    [Fact]
    public async Task ManyZeroLengthChunks_Terminate()
    {
        var zeros = Enumerable.Range(0, 5_000).SelectMany(_ => WebpFixtures.Chunk("zZzZ", [])).ToArray();
        var webp = WebpFixtures.Riff([.. zeros, .. WebpFixtures.Simple().AsSpan(12)]);

        var error = await HostileImages.Bounded(() => Record.Exception(() => ImageMetadataStripper.Strip(webp)));

        Assert.True(error is null or InvalidDataException, error?.ToString());
    }

    [Fact]
    public async Task TruncatedWebp_AtEveryLength_EndsCleanly()
    {
        var webp = WebpFixtures.PhoneWebp();
        var riffEnd = 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(4));
        for (var cut = 0; cut < riffEnd; cut += 3)
        {
            var truncated = webp[..cut];
            await HostileImages.Bounded(() => ExifCameraReader.Read(truncated));
            var error = await HostileImages.Bounded(() => Record.Exception(() => StoredPhotoSanitizer.Sanitize(truncated)));
            Assert.True(error is null or InvalidOperationException, error?.ToString());
        }
    }

    [Fact]
    public async Task RandomBytes_BehindWebpMagic_AlwaysTerminate()
    {
        var random = new Random(20260923);
        for (var i = 0; i < 2_000; i++)
        {
            var body = new byte[random.Next(0, 600)];
            random.NextBytes(body);
            if (i % 3 == 0)
            {
                // Plant plausible small chunk sizes so the walk goes deep instead of failing at once.
                for (var k = 4; k + 4 <= body.Length; k += random.Next(8, 40))
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(k), (uint)random.Next(0, 32));
                }
            }

            var riff = i % 2 == 0 ? (uint)(body.Length + 4) : (uint)random.Next();
            byte[] image = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, .. body];
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), riff);
            await HostileImages.Bounded(() => ExifCameraReader.Read(image));
            var error = await HostileImages.Bounded(() => Record.Exception(() => ImageMetadataStripper.Strip(image)));
            Assert.True(error is null or InvalidDataException, error?.ToString());
            await HostileImages.Bounded(() => StoredPhotoSanitizer.Inspect(image));
        }
    }

    /// <summary>The orientation tag in the cleaned file's EXIF chunk, read back through the sanitizer's own path.</summary>
    private static int ExifCameraReaderOrientation(byte[] webp)
    {
        var tiff = WebpFixtures.Chunks(webp).Single(c => c.FourCc == "EXIF").Payload;
        var reader = new ExifTiffReader(tiff, tiff[0] == 'I');
        return (int)(reader.Short(reader.ReadIfd(reader.U32(4)), 0x0112) ?? 0);
    }
}
