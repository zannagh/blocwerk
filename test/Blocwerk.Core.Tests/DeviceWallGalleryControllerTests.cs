// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services.DeviceWrites;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>/api/v1/walls/{w}/gallery</c> for the companion app: the camera's upload pipeline (metadata stripped, the type and
/// size limits) under the web gallery's rule (wall admins only), done once per <c>clientRequestId</c>. Who may call at
/// all is <see cref="DeviceApiRoutePostureTests"/>.
/// </summary>
public sealed class DeviceWallGalleryControllerTests
{
    [Fact]
    public async Task AnAdmin_UploadsAPhoto_StrippedOfItsLocation_OncePerClientRequestId()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var original = PrivacyPhotos.GpsJpeg();
        var key = Guid.NewGuid();

        var first = Applied(await Controller(h, original).Upload(h.WallId, key, default));
        var replay = Applied(await Controller(h, original).Upload(h.WallId, key, default));

        await using var db = h.CreateContext();
        var image = Assert.Single(await db.WallImages.Where(i => i.WallId == h.WallId).ToListAsync());
        Assert.Equal(key, first.ClientRequestId);
        Assert.Equal(image.Id, Id(first));
        Assert.Equal(image.Id, Id(replay));
        PrivacyPhotos.AssertCleanJpeg(original, await File.ReadAllBytesAsync(h.WallImageStorage.ResolvePhysicalPath(image.StoragePath)!));
    }

    [Fact]
    public async Task AMember_Is403_AnOutsider404_AndNothingIsStored()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        Refused(await Controller(h, PrivacyPhotos.GpsJpeg()).Upload(h.WallId, null, default), 403);

        h.ActingUser = await h.AddMemberAsync("moderator@test", WallRole.Moderator);
        Refused(await Controller(h, PrivacyPhotos.GpsJpeg()).Upload(h.WallId, null, default), 403);

        Refused(await Controller(h, PrivacyPhotos.GpsJpeg()).Upload(Guid.NewGuid(), null, default), 404);

        await using var db = h.CreateContext();
        Assert.Empty(await db.WallImages.ToListAsync());
    }

    [Fact]
    public async Task ABadBody_IsAPermanentRefusal_AndFreesItsKey()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var key = Guid.NewGuid();

        Refused(await Controller(h, [1, 2, 3], "application/pdf").Upload(h.WallId, key, default), 415);
        Refused(await Controller(h, PrivacyPhotos.ExifTiff(), "image/jpeg").Upload(h.WallId, key, default), 415);
        Refused(await Controller(h, PrivacyPhotos.GpsPng(), "image/jpeg").Upload(h.WallId, key, default), 415);
        Refused(await Controller(h, []).Upload(h.WallId, null, default), 400);
        Refused(await Controller(h, PrivacyPhotos.GpsJpeg()).Upload(h.WallId, Guid.Empty, default), 400);

        // The refused upload left no receipt, so the same key uploads the fixed photo.
        Applied(await Controller(h, PrivacyPhotos.GpsJpeg()).Upload(h.WallId, key, default));
        Refused(await Controller(h, PrivacyPhotos.GpsJpeg()).Upload(Guid.NewGuid(), key, default), 404);
    }

    [Fact]
    public async Task AnAdmin_DeletesAPhoto_ButNotOneOfAnotherWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var id = Id(Applied(await Controller(h, PrivacyPhotos.GpsJpeg()).Upload(h.WallId, null, default)));

        Refused(await Controller(h, []).Delete(h.WallId, Guid.NewGuid(), null, default), 404);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        Refused(await Controller(h, []).Delete(h.WallId, id, null, default), 403);
        h.ActingUser = h.Owner;
        Applied(await Controller(h, []).Delete(h.WallId, id, null, default));

        await using var db = h.CreateContext();
        Assert.Empty(await db.WallImages.ToListAsync());
    }

    private static Guid Id(OfflineActionResponse body) =>
        (Guid)body.Result!.GetType().GetProperty("imageId")!.GetValue(body.Result)!;

    private static OfflineActionResponse Applied(IActionResult result)
    {
        var body = Assert.IsType<OfflineActionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(body.Applied);
        return body;
    }

    private static void Refused(IActionResult result, int status)
    {
        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, refused.StatusCode);
        Assert.True(Assert.IsType<OfflineActionError>(refused.Value).Permanent);
    }

    private static DeviceWallGalleryController Controller(WallTestHarness h, byte[] body, string contentType = "image/jpeg")
    {
        var http = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() };
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentType = contentType;
        return new DeviceWallGalleryController(
            h.WallImageService,
            h.WallImageStorage,
            h.CurrentUser,
            new DeviceWriteReceiptService(h.DbContextFactory))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }
}
