using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Every way a wall, panel or gallery photo reaches storage strips its location at ingest — before the
/// bytes are stored or handed to detection — while keeping orientation 6 and the image data byte for byte.
/// </summary>
public class PhotoIngestPrivacyTests
{
    [Fact]
    public async Task StagePanel_StoresAndDetectsOnTheCleanPhoto()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await CapturePanelSeed.SeedGridWithBoulderAsync(h);
        WallUpdateSessionFixture.NoDetections(h);
        var original = PrivacyPhotos.GpsJpeg();

        await WallUpdateSessionFixture.Panels(h).StagePanelAsync(h.WallId, 0, 1, original, "image/jpeg");

        await using var db = h.CreateContext();
        var stored = await db.WallPanels.Where(p => p.Col == 0 && p.Row == 1).Select(p => p.StagedPhoto).SingleAsync();
        PrivacyPhotos.AssertCleanJpeg(original, stored!);
        await h.HoldDetection.Received(1).DetectHoldsAsync(
            Arg.Is<byte[]>(b => b.SequenceEqual(stored!)), Arg.Any<HoldDetectionParameters?>());
    }

    [Fact]
    public async Task BigUpdateStage_StoresEveryPanelClean()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        WallUpdateSessionFixture.NoDetections(h);
        var original = PrivacyPhotos.GpsJpeg();

        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, [new BigUpdatePhoto(original, "image/jpeg", 0, 0)]);

        await using var db = h.CreateContext();
        var stored = await db.WallPanels.Where(p => p.StagedPhoto != null).Select(p => p.StagedPhoto).SingleAsync();
        PrivacyPhotos.AssertCleanJpeg(original, stored!);
        await h.HoldDetection.DidNotReceive().DetectHoldsAsync(
            Arg.Is<byte[]>(b => b.SequenceEqual(original)), Arg.Any<HoldDetectionParameters?>());
    }

    [Fact]
    public async Task LegacyWallPhotoUpload_StoresTheCleanPhoto()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var original = PrivacyPhotos.GpsJpeg();

        await h.WallService.UploadPhotoAsync(h.WallId, original, "image/jpeg", autoDetect: false);

        await using var db = h.CreateContext();
        var stored = await db.Walls.Where(w => w.Id == h.WallId).Select(w => w.Photo).SingleAsync();
        PrivacyPhotos.AssertCleanJpeg(original, stored!);
    }

    [Fact]
    public async Task LegacyWallPhotoUpload_RefusesABrokenJpeg_AndKeepsTheOldPhoto()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.WallService.UploadPhotoAsync(h.WallId, PrivacyPhotos.GpsJpeg()[..300], "image/jpeg", autoDetect: false));

        Assert.Contains("damaged", ex.Message);
        await using var db = h.CreateContext();
        Assert.Equal([1, 2, 3], await db.Walls.Where(w => w.Id == h.WallId).Select(w => w.Photo).SingleAsync());
    }

    [Fact]
    public async Task GalleryUpload_WritesTheCleanFile_AndRecordsItsRealSize()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var original = PrivacyPhotos.GpsJpeg();

        var result = await GalleryUploadAsync(h, original, "image/jpeg");

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(result).StatusCode);
        await using var db = h.CreateContext();
        var row = await db.WallImages.SingleAsync();
        var stored = await File.ReadAllBytesAsync(h.WallImageStorage.ResolvePhysicalPath(row.StoragePath)!);
        PrivacyPhotos.AssertCleanJpeg(original, stored);
        Assert.Equal(stored.LongLength, row.SizeBytes);
    }

    [Fact]
    public async Task GalleryUpload_OfAPngWithAnExifChunk_IsCleaned()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var original = PrivacyPhotos.GpsPng();

        await GalleryUploadAsync(h, original, "image/png");

        await using var db = h.CreateContext();
        var row = await db.WallImages.SingleAsync();
        var stored = await File.ReadAllBytesAsync(h.WallImageStorage.ResolvePhysicalPath(row.StoragePath)!);
        PrivacyPhotos.AssertNoLocation(stored);
        Assert.Equal(PrivacyPhotos.PngImageData(original), PrivacyPhotos.PngImageData(stored));
    }

    [Fact]
    public async Task GalleryUpload_OfABrokenJpeg_Is400_AndStoresNothing()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        var result = await GalleryUploadAsync(h, PrivacyPhotos.GpsJpeg()[..300], "image/jpeg");

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ObjectResult>(result).StatusCode);
        await using var db = h.CreateContext();
        Assert.Equal(0, await db.WallImages.CountAsync());
    }

    /// <summary>The Pi's raw-body upload: the whole request body is the image.</summary>
    private static async Task<IActionResult> GalleryUploadAsync(WallTestHarness h, byte[] body, string contentType)
    {
        var controller = new WallImagesController(h.WallImageService, h.WallImageStorage, Substitute.For<ICurrentUserService>());
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "pi"),
                new Claim(ClaimTypes.NameIdentifier, "1"),
                new Claim(ApiKeyClaimTypes.Scope, ApiKeyScope.Wall.ToString()),
                new Claim(ApiKeyClaimTypes.ApiKeyId, Guid.NewGuid().ToString()),
                new Claim(ApiKeyClaimTypes.WallId, h.WallId.ToString()),
            ],
            ApiKeyAuthenticationHandler.SchemeName);
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        http.Request.ContentType = contentType;
        http.Request.Body = new MemoryStream(body);
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return await controller.Upload(h.WallId, CancellationToken.None);
    }
}
