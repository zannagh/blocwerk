// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services.DeviceWrites;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>PUT /api/v1/walls/{w}/photo</c> for the companion app: the web's empty-wall upload for admins, with the gallery's
/// hardening (type by magic bytes, pixel limit, metadata stripped, HEIC converted) and a required replay key.
/// </summary>
public sealed class DeviceWallPhotoControllerTests
{
    [Fact]
    public async Task AnAdmin_UploadsTheFirstPhoto_Once_StrippedOfItsLocation()
    {
        using var h = new WallTestHarness();
        var wallId = await EmptyWallAsync(h);
        var original = PrivacyPhotos.GpsJpeg();
        var key = Guid.NewGuid();

        var first = Sha(await Controller(h, original, "image/jpeg").Upload(wallId, key, [], default));
        var replay = Sha(await Controller(h, original, "image/jpeg").Upload(wallId, key, [], default));

        Assert.NotNull(first);
        Assert.Equal(first, replay);
        await using var db = h.CreateContext();
        var stored = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId).Select(w => w.Photo).SingleAsync();
        PrivacyPhotos.AssertCleanJpeg(original, stored!);
        await h.HoldDetection.Received(1).DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>());
    }

    [Fact]
    public async Task AWallWithAPhoto_Is409_AndABadBodyIsRefused()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0); // has a photo
        var empty = await EmptyWallAsync(h);

        Refused(await Controller(h, PrivacyPhotos.GpsJpeg(), "image/jpeg").Upload(h.WallId, Guid.NewGuid(), [], default), 409);
        Refused(await Controller(h, PrivacyPhotos.ExifTiff(), "image/jpeg").Upload(empty, Guid.NewGuid(), [], default), 415);
        Refused(await Controller(h, PrivacyPhotos.GpsPng(), "image/jpeg").Upload(empty, Guid.NewGuid(), [], default), 415);
        Refused(await Controller(h, Heic(), "image/heic").Upload(empty, Guid.NewGuid(), [], default), 415); // no converter
        Refused(await Controller(h, PrivacyPhotos.GpsJpeg(), "image/jpeg").Upload(empty, null, [], default), 400);
    }

    [Fact]
    public async Task AHeic_IsConvertedToAJpegWithoutMetadata()
    {
        using var h = new WallTestHarness();
        var wallId = await EmptyWallAsync(h);
        var converter = Substitute.For<ICapturePhotoConverter>();
        converter.ToJpegAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(PrivacyPhotos.GpsJpeg());

        Assert.NotNull(Sha(await Controller(h, Heic(), "image/heic").Upload(wallId, Guid.NewGuid(), [converter], default)));

        await using var db = h.CreateContext();
        var wall = await db.Walls.IgnoreQueryFilters().SingleAsync(w => w.Id == wallId);
        Assert.Equal("image/jpeg", wall.PhotoContentType);
        PrivacyPhotos.AssertNoLocation(wall.Photo!);
    }

    [Fact]
    public async Task AMember_Gets403_AnOutsider404()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Moderator);
        Refused(await Controller(h, PrivacyPhotos.GpsJpeg(), "image/jpeg").Upload(h.WallId, Guid.NewGuid(), [], default), 403);

        h.ActingUser = h.Owner;
        Assert.IsType<NotFoundResult>(await Controller(h, PrivacyPhotos.GpsJpeg(), "image/jpeg").Upload(Guid.NewGuid(), Guid.NewGuid(), [], default));
    }

    [Fact]
    public async Task TwoConcurrentUploads_RunHoldDetectionOnce_TheOtherIs409()
    {
        using var h = new WallTestHarness();
        var wallId = await EmptyWallAsync(h);
        var first = Controller(h, PrivacyPhotos.GpsJpeg(), "image/jpeg");
        var second = Controller(h, PrivacyPhotos.GpsJpeg(), "image/jpeg");
        var release = new TaskCompletionSource();
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(async _ =>
            {
                await release.Task;
                return new List<DetectedHold>();
            });

        var a = first.Upload(wallId, Guid.NewGuid(), [], default);
        var b = second.Upload(wallId, Guid.NewGuid(), [], default);
        await Task.Delay(200);
        release.SetResult();
        var results = await Task.WhenAll(a, b);

        Assert.Single(results.OfType<OkObjectResult>());
        Refused(Assert.Single(results.OfType<ObjectResult>().Where(r => r is not OkObjectResult)), 409);
        await h.HoldDetection.Received(1).DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>());
    }

    [Fact]
    public async Task AHeicOverThePixelLimit_IsRefusedBeforeConversion()
    {
        using var h = new WallTestHarness();
        var wallId = await EmptyWallAsync(h);
        var converter = Substitute.For<ICapturePhotoConverter>();

        Refused(await Controller(h, Heic(60_000, 60_000), "image/heic").Upload(wallId, Guid.NewGuid(), [converter], default), 400);
        Refused(await Controller(h, Heic(), "image/heic").Upload(wallId, Guid.NewGuid(), [converter], default), 400); // no sizes at all

        await converter.DidNotReceive().ToJpegAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TheHeicHeader_NamesTheLargestImage()
    {
        Assert.Equal((4032, 3024), HeicHeader.LargestImage(Heic(4032, 3024)));
        Assert.Equal((4032, 3024), HeicHeader.LargestImage([.. Heic(512, 512), .. Ispe(4032, 3024)]));
        Assert.Null(HeicHeader.LargestImage(Heic()));
    }

    // An ftyp box naming HEIC, then (with a size) one ispe property box.
    private static byte[] Heic(uint width, uint height) => [.. Heic(), .. Ispe(width, height)];

    private static byte[] Ispe(uint width, uint height)
    {
        var box = new byte[20];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box, 20);
        "ispe"u8.CopyTo(box.AsSpan(4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(12), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(16), height);
        return box;
    }

    private static byte[] Heic() => [0, 0, 0, 0x18, .. "ftypheic"u8, 0, 0, 0, 0, .. "mif1heic"u8];

    private static string? Sha(IActionResult result) =>
        Assert.IsType<DeviceWallPhotoResponse>(Assert.IsType<OkObjectResult>(result).Value).PhotoSha;

    private static void Refused(IActionResult result, int status)
    {
        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, refused.StatusCode);
        Assert.IsType<ApiErrorResponse>(refused.Value);
    }

    // A wall of the harness owner with no photo and no panels yet.
    private static async Task<Guid> EmptyWallAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        if (!await db.Users.AnyAsync(u => u.Id == h.Owner.Id))
        {
            db.Users.Add(h.Owner);
        }

        var wall = new Wall { Name = "Empty", OwnerId = h.Owner.Id };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });
        await db.SaveChangesAsync();
        return wall.Id;
    }

    private static DeviceWallPhotoController Controller(WallTestHarness h, byte[] body, string contentType)
    {
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(new List<DetectedHold> { new(0.25, 0.5, 0.03, "red", 0.9) }));
        var http = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() };
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentType = contentType;
        http.Request.ContentLength = body.Length;
        return new DeviceWallPhotoController(
            h.DbContextFactory,
            h.CurrentUser,
            h.WallService,
            new AssetHashReader(h.DbContextFactory, h.WallService),
            new DeviceWriteReceiptService(h.DbContextFactory))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }
}
