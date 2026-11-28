// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net.Http.Headers;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services.DeviceWrites;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>/api/v1/walls/{w}/boulders/{b}/beta-videos</c> for the companion app: the browser's streamed upload (members of
/// the wall), done once per <c>clientRequestId</c> and capped for one request, the carousel's list with playback URLs,
/// and the uploader's delete. Who may call at all is <see cref="DeviceApiRoutePostureTests"/>.
/// </summary>
public sealed class DeviceBetaVideosControllerTests
{
    private static readonly byte[] Clip = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70];

    [Fact]
    public async Task AMember_UploadsAClipOnce_AndListsIt_WithItsPlaybackUrls()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedAsync(h);
        h.ActingUser = await h.AddMemberAsync("member@test", Enums.WallRole.Member);
        var key = Guid.NewGuid();

        var first = Applied(await Upload(h, boulder, key));
        var replay = Applied(await Upload(h, boulder, key));

        var list = Assert.IsType<List<DeviceBetaVideoResponse>>(
            Assert.IsType<OkObjectResult>(await Controller(h).List(h.WallId, boulder)).Value);
        var clip = Assert.Single(list);
        Assert.Equal(clip.Id, VideoId(first));
        Assert.Equal(clip.Id, VideoId(replay));
        Assert.True(clip.Mine);
        Assert.Equal($"/api/v1/beta-videos/{clip.Id}", clip.VideoUrl);
        Assert.Equal("Pending", clip.EncodingStatus);
        Assert.Null(clip.HlsUrl);
    }

    [Fact]
    public async Task AnOutsider_AnotherWall_AndAnUnknownBoulder_Are404_AndNothingIsStored()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedAsync(h);

        Refused(await Upload(h, boulder, null, wallId: Guid.NewGuid()), 404);
        Refused(await Upload(h, Guid.NewGuid(), null), 404);
        Assert.IsType<NotFoundResult>(await Controller(h).List(Guid.NewGuid(), boulder));

        h.ActingUser = await OutsiderAsync(h);
        Refused(await Upload(h, boulder, null), 404);
        Assert.IsType<NotFoundResult>(await Controller(h).List(h.WallId, boulder));

        await using var db = h.CreateContext();
        Assert.Empty(await db.BetaVideos.ToListAsync());
    }

    [Fact]
    public async Task AnUploadWithoutALength_OverTheCap_OrNotAVideo_IsAPermanentRefusal()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedAsync(h);

        Refused(await Upload(h, boulder, null, contentLength: null), 411);
        Refused(await Upload(h, boulder, null, contentLength: DeviceBetaVideosController.MaxUploadBytes + 1), 413);
        Refused(await Upload(h, boulder, null, contentType: "image/jpeg"), 400);
        Refused(await Upload(h, boulder, Guid.Empty), 400);
    }

    [Fact]
    public async Task TheUploader_DeletesTheirClip_AnotherMemberCannot()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedAsync(h);
        var id = VideoId(Applied(await Upload(h, boulder, null)));

        h.ActingUser = await h.AddMemberAsync("member@test", Enums.WallRole.Member);
        Refused(await Controller(h).Delete(h.WallId, boulder, id, null), 404);
        h.ActingUser = h.Owner;
        Applied(await Controller(h).Delete(h.WallId, boulder, id, null));
        Refused(await Controller(h).Delete(h.WallId, boulder, id, null), 404);
    }

    private static Task<IActionResult> Upload(
        WallTestHarness h,
        Guid boulder,
        Guid? key,
        Guid? wallId = null,
        long? contentLength = -1,
        string contentType = "video/mp4")
    {
        var controller = Controller(h);
        using var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(Clip);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(part, "file", "beta.mp4");
        var body = new MemoryStream();
        content.CopyTo(body, null, CancellationToken.None);
        body.Position = 0;
        controller.Request.Body = body;
        controller.Request.ContentType = content.Headers.ContentType!.ToString();
        controller.Request.ContentLength = contentLength == -1 ? body.Length : contentLength;
        return controller.Upload(
            wallId ?? h.WallId, boulder, key, h.BetaStorage, new BlocwerkSettings(), NullLoggerFactory.Instance, default);
    }

    private static Guid VideoId(OfflineActionResponse body) =>
        (Guid)body.Result!.GetType().GetProperty("videoId")!.GetValue(body.Result)!;

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

    private static DeviceBetaVideosController Controller(WallTestHarness h) =>
        new(h.BoulderService, h.BetaVideoService, h.CurrentUser, new DeviceWriteReceiptService(h.DbContextFactory))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() },
            },
        };

    private static async Task<User> OutsiderAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var user = new User { Identifier = "outsider@test", DisplayName = "Outsider" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Guid> SeedAsync(WallTestHarness h)
    {
        await h.SeedWallAsync(holdCount: 0);
        return await VisitFixture.SeedBoulderAsync(h, h.WallId);
    }
}
