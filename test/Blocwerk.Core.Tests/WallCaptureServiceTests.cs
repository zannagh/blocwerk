using System.Text;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The admin-facing capture service: authorization (wall admins only, never a kiosk, the wall taken
/// from the capture row), upload validation, metadata stripping on disk, and the declaration pre-fill.
/// </summary>
public class WallCaptureServiceTests
{
    [Fact]
    public async Task NonAdminMember_IsRefused_AndCannotTouchAnAdminsCapture()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.CreateDraftAsync(h.WallId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.GetCapturesAsync(h.WallId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.GetCaptureAsync(captureId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.Service.AddPhotoAsync(captureId, "x.jpg", CaptureScenario.TinyJpeg(9), CancellationToken.None));
    }

    [Fact]
    public async Task Kiosk_IsRefused_EvenOnItsOwnWallActingAsTheOwner()
    {
        using var h = new WallTestHarness();
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(_ => h.WallId);
        using var s = new CaptureScenario(h, kiosk: kiosk);
        await h.SeedWallAsync(holdCount: 0);

        await Assert.ThrowsAsync<KioskRestrictedException>(() => s.Service.CreateDraftAsync(h.WallId));
        await Assert.ThrowsAsync<KioskRestrictedException>(() => s.Service.GetCapturesAsync(h.WallId));
    }

    [Fact]
    public async Task Draft_IsRefused_OnAWallWithoutMarkers()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        await h.SeedWallAsync(holdCount: 0);

        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => s.Service.CreateDraftAsync(h.WallId));
        Assert.Contains("markers", ex.Message);
    }

    [Theory]
    [InlineData("heic")]
    [InlineData("mif1")]
    public async Task HeicUpload_IsRefused_WithAClearMessage(string brand)
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var draft = await OpenDraftAsync(h, s);
        byte[] heic = [0, 0, 0, 24, .. "ftyp"u8.ToArray(), .. Encoding.ASCII.GetBytes(brand), 0, 0, 0, 0, .. new byte[64]];

        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => s.Service.AddPhotoAsync(draft, "IMG_1.HEIC", heic, CancellationToken.None));
        Assert.Contains("HEIC", ex.Message);
    }

    [Fact]
    public async Task NonImage_AndDuplicate_AreRefused()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var draft = await OpenDraftAsync(h, s);
        await s.Service.AddPhotoAsync(draft, "a.jpg", CaptureScenario.TinyJpeg(1), CancellationToken.None);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => s.Service.AddPhotoAsync(draft, "notes.txt", "hello"u8.ToArray(), CancellationToken.None));
        var dup = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => s.Service.AddPhotoAsync(draft, "a-again.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(1)), CancellationToken.None));
        Assert.Contains("already uploaded", dup.Message);
    }

    [Fact]
    public async Task Upload_ReadsExifCameraFacts_ButStoresTheBytesWithoutAnyMetadata()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var draft = await OpenDraftAsync(h, s);

        var result = await s.Service.AddPhotoAsync(draft, "IMG_2770.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg()), CancellationToken.None);

        Assert.Equal(14, result.Focal35mm);
        Assert.Equal([0, 1, 2, 6, 7, 12], result.MarkerIds);
        await using var db = h.CreateContext();
        var photo = await db.WallCapturePhotos.SingleAsync();
        Assert.Matches("^cam-[0-9a-f]{24}$", photo.CameraGroup);
        Assert.Equal(ExifCameraReader.Read(ExifJpeg.Build(CaptureScenario.TinyJpeg(seed: 5))).CameraGroup(64, 48), photo.CameraGroup);
        Assert.NotEqual(ExifCameraReader.Read(ExifJpeg.Build(CaptureScenario.TinyJpeg(), lens: "other lens")).CameraGroup(64, 48), photo.CameraGroup);
        Assert.Equal((64, 48), (photo.Width, photo.Height));
        var stored = await File.ReadAllBytesAsync(s.Files.ResolvePhysicalPath(photo.StoredPath)!);
        Assert.Equal(-1, stored.AsSpan().IndexOf("Exif\0\0"u8));
        Assert.Equal(-1, stored.AsSpan().IndexOf(ExifJpeg.GpsLatitudeBytes));
        Assert.Equal(CaptureScenario.TinyJpeg(), stored);
    }

    [Fact]
    public async Task Declarations_ArePrefilledFromTheWallsPreviousCapture()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var first = await s.StartCaptureAsync(levelPairs: "14-15");
        await s.Processor.ProcessAsync(first, CancellationToken.None);

        var draft = await s.Service.CreateDraftAsync(h.WallId);
        await s.Service.AddPhotoAsync(draft.CaptureId, "c.jpg", CaptureScenario.TinyJpeg(7), CancellationToken.None);
        var suggested = await s.Service.SuggestDeclarationsAsync(draft.CaptureId);

        Assert.Equal([0, 1, 2], suggested.Segments.Select(x => x.Index));
        Assert.Equal(45, suggested.Segments[0].DeclaredAngleDeg);
        Assert.True(suggested.Segments[1].VerticalReference);
        Assert.Equal("14-15", CaptureDeclarationRules.FormatLevelPairs(suggested.LevelPairs));
    }

    [Theory]
    [InlineData("14-15, 8-9", null)]
    [InlineData("14-14", "different")]
    [InlineData("14-99", "different")]
    [InlineData("abc", "different")]
    public void LevelPairs_Parse(string text, string? error)
    {
        var (pairs, message) = CaptureDeclarationRules.ParseLevelPairs(text);
        if (error is null)
        {
            Assert.Null(message);
            Assert.Equal(2, pairs.Count);
        }
        else
        {
            Assert.Contains(error, message);
        }
    }

    private static async Task<Guid> OpenDraftAsync(WallTestHarness h, CaptureScenario s)
    {
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        return (await s.Service.CreateDraftAsync(h.WallId)).CaptureId;
    }
}
