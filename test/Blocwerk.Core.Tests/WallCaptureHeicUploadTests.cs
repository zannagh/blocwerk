// <copyright file="WallCaptureHeicUploadTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text;
using Blocwerk.Core.Capture;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// HEIC uploads (the iPhone default, and what a Mac's file picker hands over unconverted): converted to an
/// upright JPEG, EXIF camera facts read from the conversion, nothing but the stripped JPEG stored.
/// </summary>
public class WallCaptureHeicUploadTests
{
    private static readonly byte[] Heic = [0, 0, 0, 24, .. "ftypheic"u8.ToArray(), 0, 0, 0, 0, .. new byte[64]];

    [Fact]
    public async Task HeicUpload_IsConvertedToJpeg_ReadsItsExif_AndStoresItStripped()
    {
        using var h = new WallTestHarness();
        var converter = new FakeConverter(ExifJpeg.Build(CaptureScenario.TinyJpeg()));
        using var s = new CaptureScenario(h, photoConverter: converter);
        var draft = await OpenDraftAsync(h, s);

        var result = await s.Service.AddPhotoAsync(draft, "IMG_2787.HEIC", Heic, CancellationToken.None);

        Assert.Equal(Heic, converter.Received);
        Assert.Equal(14, result.Focal35mm);
        await using var db = h.CreateContext();
        var photo = await db.WallCapturePhotos.SingleAsync();
        Assert.Equal("image/jpeg", photo.ContentType);
        Assert.EndsWith(".jpg", photo.StoredPath, StringComparison.Ordinal);
        Assert.Equal((64, 48), (photo.Width, photo.Height));
        var stored = await File.ReadAllBytesAsync(s.Files.ResolvePhysicalPath(photo.StoredPath)!);
        Assert.Equal(-1, stored.AsSpan().IndexOf("Exif\0\0"u8));
        Assert.Equal(-1, stored.AsSpan().IndexOf(ExifJpeg.GpsLatitudeBytes));
    }

    [Fact]
    public async Task IphoneJpeg_KeepsOnlyTheGravityVector_TheStoredPhotoHasNoMakerNote()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var draft = await OpenDraftAsync(h, s);
        var tiff = AppleMakerNoteExif.Tiff(AppleMakerNoteExif.MakerNote((-0.25, -0.95, -0.125)));

        await s.Service.AddPhotoAsync(draft, "IMG_1.jpg", AppleMakerNoteExif.Jpeg(CaptureScenario.TinyJpeg(), tiff), CancellationToken.None);

        await using var db = h.CreateContext();
        var photo = await db.WallCapturePhotos.SingleAsync();
        Assert.Equal((-0.25, -0.95, -0.125), (photo.DeviceGravityX, photo.DeviceGravityY, photo.DeviceGravityZ));
        var stored = await File.ReadAllBytesAsync(s.Files.ResolvePhysicalPath(photo.StoredPath)!);
        Assert.Equal(-1, stored.AsSpan().IndexOf("Apple iOS"u8));
        Assert.Null(DeviceGravityReader.Read(stored));
    }

    [Fact]
    public async Task HeicUpload_ReadsTheGravityFromTheHeic_WhenTheConversionDroppedIt()
    {
        using var h = new WallTestHarness();
        var heic = AppleMakerNoteExif.Heic(AppleMakerNoteExif.Tiff(AppleMakerNoteExif.MakerNote((-0.5, 0.25, -0.75))));
        using var s = new CaptureScenario(h, photoConverter: new FakeConverter(ExifJpeg.Build(CaptureScenario.TinyJpeg())));
        var draft = await OpenDraftAsync(h, s);

        await s.Service.AddPhotoAsync(draft, "IMG_2.HEIC", heic, CancellationToken.None);

        await using var db = h.CreateContext();
        var photo = await db.WallCapturePhotos.SingleAsync();
        Assert.Equal((-0.5, 0.25, -0.75), (photo.DeviceGravityX, photo.DeviceGravityY, photo.DeviceGravityZ));
    }

    [Fact]
    public async Task PhotoWithoutAppleMakerNote_HasNoGravity()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var draft = await OpenDraftAsync(h, s);

        await s.Service.AddPhotoAsync(draft, "android.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg()), CancellationToken.None);

        await using var db = h.CreateContext();
        var photo = await db.WallCapturePhotos.SingleAsync();
        Assert.Null(photo.DeviceGravityX ?? photo.DeviceGravityY ?? photo.DeviceGravityZ);
    }

    [Fact]
    public async Task FailedConversion_IsRefused_WithAClearMessage()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, photoConverter: new FakeConverter(null));
        var draft = await OpenDraftAsync(h, s);

        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => s.Service.AddPhotoAsync(draft, "IMG_1.HEIC", Heic, CancellationToken.None));

        Assert.Contains("could not be converted", ex.Message);
        await using var db = h.CreateContext();
        Assert.False(await db.WallCapturePhotos.AnyAsync());
    }

    [Fact]
    public async Task ConverterOutputThatIsNotAJpeg_IsRefused()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, photoConverter: new FakeConverter(Encoding.ASCII.GetBytes("not a jpeg")));
        var draft = await OpenDraftAsync(h, s);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => s.Service.AddPhotoAsync(draft, "IMG_1.HEIC", Heic, CancellationToken.None));
    }

    [Fact]
    public void HeifConvert_IsCalledWithQualityInputAndOutput()
    {
        Assert.Equal(["-q", "92", "/tmp/a.heic", "/tmp/a.jpg"], HeifCapturePhotoConverter.Arguments("/tmp/a.heic", "/tmp/a.jpg"));
    }

    private static async Task<Guid> OpenDraftAsync(WallTestHarness h, CaptureScenario s)
    {
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        return (await s.Service.CreateDraftAsync(h.WallId)).CaptureId;
    }

    /// <summary>Returns <paramref name="jpeg"/>, or fails like heif-convert on a broken file when it is null.</summary>
    private sealed class FakeConverter(byte[]? jpeg) : ICapturePhotoConverter
    {
        public byte[]? Received { get; private set; }

        public Task<byte[]> ToJpegAsync(byte[] heic, CancellationToken ct)
        {
            Received = heic;
            return jpeg is null ? Task.FromException<byte[]>(new InvalidDataException("broken")) : Task.FromResult(jpeg);
        }
    }
}
