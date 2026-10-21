using System.Security.Claims;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Texture bytes and the 3D view's texture list have the visibility of the wall itself: members,
/// share-link holders and the wall's own kiosk — nobody else, and never another wall's model.
/// </summary>
public class WallGeometryTextureAccessTests
{
    [Fact]
    public async Task Member_GetsTheTexture_Revalidated_EvenUnderItsVersionedUrl()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var (modelId, _) = await ComputeModelAsync(h, s);

        var (result, http) = await ServeAsync(h, s, h.WallId, modelId, "0", token: null, Kiosk(null));

        var file = Assert.IsType<FileContentHttpResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal("private, no-cache", http.Response.Headers.CacheControl.ToString());

        var url = (await s.Service.GetActiveTexturesAsync(h.WallId))[0].Url;
        var versioned = AssetRequests.For(url);
        await WallGeometryTextureEndpoints.HandleAsync(
            h.WallId, modelId, "0", null, new ClaimsPrincipal(), versioned, h.WallService, h.CurrentUser,
            h.DbContextFactory, Kiosk(null), s.Files, CancellationToken.None);
        Assert.Equal("private, no-cache", versioned.Response.Headers.CacheControl.ToString());
    }

    /// <summary>
    /// Every reuse revalidates, so the 304 is the gate: a holder of the texture's ETag who may not see the wall (an
    /// anonymous caller, a stranger's kiosk) gets the 404, never a 304; the member still gets the 304.
    /// </summary>
    [Fact]
    public async Task ARevalidation_IsAnsweredOnlyAfterTheGate()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var (modelId, _) = await ComputeModelAsync(h, s);
        var (_, member) = await ServeAsync(h, s, h.WallId, modelId, "0", token: null, Kiosk(null));
        var etag = member.Response.Headers.ETag.ToString();
        Assert.False(string.IsNullOrEmpty(etag));

        async Task<IResult> RevalidateAsync(IKioskContext kiosk)
        {
            var http = new DefaultHttpContext();
            http.Request.Headers.IfNoneMatch = etag;
            return await WallGeometryTextureEndpoints.HandleAsync(
                h.WallId, modelId, "0", null, new ClaimsPrincipal(), http, h.WallService, h.CurrentUser,
                h.DbContextFactory, kiosk, s.Files, CancellationToken.None);
        }

        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await RevalidateAsync(Kiosk(null))).StatusCode);
        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromException<User>(new UnauthorizedAccessException()));
        Assert.IsType<NotFound>(await RevalidateAsync(Kiosk(null)));
        Assert.IsType<NotFound>(await RevalidateAsync(Kiosk(Guid.NewGuid())));
    }

    [Fact]
    public async Task Anonymous_WithoutToken_GetsNotFound_ButTheShareTokenAndOwnKioskGetIn()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var (modelId, _) = await ComputeModelAsync(h, s);
        await using (var db = h.CreateContext())
        {
            (await db.Walls.SingleAsync()).ShareToken = "share-me";
            await db.SaveChangesAsync();
        }

        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromException<User>(new UnauthorizedAccessException()));

        Assert.IsType<NotFound>((await ServeAsync(h, s, h.WallId, modelId, "0", null, Kiosk(null))).Result);
        Assert.IsType<NotFound>((await ServeAsync(h, s, h.WallId, modelId, "0", "wrong", Kiosk(null))).Result);
        Assert.IsType<NotFound>((await ServeAsync(h, s, h.WallId, modelId, "0", null, Kiosk(Guid.NewGuid()))).Result);
        Assert.IsType<FileContentHttpResult>((await ServeAsync(h, s, h.WallId, modelId, "0", "share-me", Kiosk(null))).Result);
        Assert.IsType<FileContentHttpResult>((await ServeAsync(h, s, h.WallId, modelId, "0", null, Kiosk(h.WallId))).Result);
    }

    [Fact]
    public async Task OutsiderMember_AndAModelOfAnotherWall_GetNotFound()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var (modelId, _) = await ComputeModelAsync(h, s);

        Assert.IsType<NotFound>((await ServeAsync(h, s, Guid.NewGuid(), modelId, "0", null, Kiosk(null))).Result);
        Assert.IsType<NotFound>((await ServeAsync(h, s, h.WallId, Guid.NewGuid(), "0", null, Kiosk(null))).Result);

        await using (var db = h.CreateContext())
        {
            db.Users.Add(new User { Identifier = "stranger@test", DisplayName = "Stranger" });
            await db.SaveChangesAsync();
            h.ActingUser = await db.Users.SingleAsync(u => u.Identifier == "stranger@test");
        }

        Assert.IsType<NotFound>((await ServeAsync(h, s, h.WallId, modelId, "0", null, Kiosk(null))).Result);
        Assert.Empty(await s.Service.GetActiveTexturesAsync(h.WallId));
    }

    [Fact]
    public async Task ActiveTextureList_GivesFacetBoundsAndUrls_CarryingTheShareToken()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var (modelId, _) = await ComputeModelAsync(h, s);

        var list = await s.Service.GetActiveTexturesAsync(h.WallId);
        Assert.Equal(["0", "5a"], list.Select(t => t.FacetId));
        Assert.StartsWith($"/api/walls/{h.WallId}/geometry/{modelId}/textures/0?v=", list[0].Url);
        Assert.StartsWith($"/api/walls/{h.WallId}/geometry/{modelId}/textures/0/mask?v=", list[0].MaskUrl);
        Assert.Null(list[1].MaskUrl);
        Assert.Equal((-100, 3000, -100, 2500), (list[0].AMin, list[0].AMax, list[0].BMin, list[0].BMax));

        await using (var db = h.CreateContext())
        {
            (await db.Walls.SingleAsync()).ShareToken = "tok en";
            await db.SaveChangesAsync();
        }

        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromException<User>(new UnauthorizedAccessException()));
        var shared = await s.Service.GetActiveTexturesAsync(h.WallId, "tok en");
        Assert.Contains("/textures/0?token=tok%20en&v=", shared[0].Url);
        Assert.Contains("/mask?token=tok%20en&v=", shared[0].MaskUrl);
        Assert.Empty(await s.Service.GetActiveTexturesAsync(h.WallId));
    }

    [Fact]
    public async Task Mask_HasTheTexturesGate_AndIsNotFoundWhereThereIsNone()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var (modelId, _) = await ComputeModelAsync(h, s);

        var (result, http) = await ServeMaskAsync(h, s, h.WallId, modelId, "0", null, Kiosk(null));
        var file = Assert.IsType<FileContentHttpResult>(result);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(FakeComputeJobClient.MaskPng, file.FileContents.ToArray());
        Assert.Equal("private, no-cache", http.Response.Headers.CacheControl.ToString());
        Assert.IsType<NotFound>((await ServeMaskAsync(h, s, h.WallId, modelId, "5a", null, Kiosk(null))).Result);
        Assert.IsType<NotFound>((await ServeMaskAsync(h, s, Guid.NewGuid(), modelId, "0", null, Kiosk(null))).Result);

        await using (var db = h.CreateContext())
        {
            (await db.Walls.SingleAsync()).ShareToken = "share-me";
            await db.SaveChangesAsync();
        }

        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromException<User>(new UnauthorizedAccessException()));
        Assert.IsType<NotFound>((await ServeMaskAsync(h, s, h.WallId, modelId, "0", null, Kiosk(null))).Result);
        Assert.IsType<NotFound>((await ServeMaskAsync(h, s, h.WallId, modelId, "0", null, Kiosk(Guid.NewGuid()))).Result);
        Assert.IsType<FileContentHttpResult>((await ServeMaskAsync(h, s, h.WallId, modelId, "0", "share-me", Kiosk(null))).Result);
        Assert.IsType<FileContentHttpResult>((await ServeMaskAsync(h, s, h.WallId, modelId, "0", null, Kiosk(h.WallId))).Result);
    }

    private static async Task<(Guid ModelId, Guid CaptureId)> ComputeModelAsync(WallTestHarness h, CaptureScenario s)
    {
        var captureId = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        await UnhashedGeometry.ClearAsync(h);
        await using var db = h.CreateContext();
        return ((await db.WallGeometryModels.SingleAsync()).Id, captureId);
    }

    private static async Task<(IResult Result, HttpContext Http)> ServeAsync(
        WallTestHarness h, CaptureScenario s, Guid wallId, Guid modelId, string facet, string? token, IKioskContext kiosk)
    {
        var http = new DefaultHttpContext();
        var result = await WallGeometryTextureEndpoints.HandleAsync(
            wallId, modelId, facet, token, new ClaimsPrincipal(), http, h.WallService, h.CurrentUser,
            h.DbContextFactory, kiosk, s.Files, CancellationToken.None);
        return (result, http);
    }

    private static async Task<(IResult Result, HttpContext Http)> ServeMaskAsync(
        WallTestHarness h, CaptureScenario s, Guid wallId, Guid modelId, string facet, string? token, IKioskContext kiosk)
    {
        var http = new DefaultHttpContext();
        var result = await WallGeometryTextureEndpoints.HandleMaskAsync(
            wallId, modelId, facet, token, new ClaimsPrincipal(), http, h.WallService, h.CurrentUser,
            h.DbContextFactory, kiosk, s.Files, CancellationToken.None);
        return (result, http);
    }

    private static IKioskContext Kiosk(Guid? wallId)
    {
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(wallId is not null);
        kiosk.KioskWallId.Returns(wallId);
        return kiosk;
    }
}
