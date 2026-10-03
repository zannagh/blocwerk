// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The splat bytes and the 3D view's imagery (facet textures + photo-real splat) have the
/// visibility of the wall itself: members, share-link holders and the wall's own kiosk — nobody
/// else, and never through another wall's URL.
/// </summary>
public class WallGeometrySplatAccessTests
{
    [Fact]
    public async Task Member_GetsTheSplat_StreamedImmutable_AndARevalidationIsA304()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var modelId = await ComputeWithSplatAsync(h, s);

        var (result, http) = await ServeAsync(h, s, h.WallId, modelId, token: null, Kiosk(null));

        var file = Assert.IsType<PhysicalFileHttpResult>(result);
        Assert.Equal("application/octet-stream", file.ContentType);
        Assert.True(file.EnableRangeProcessing);
        Assert.Equal(s.SplatClient.Spz, await File.ReadAllBytesAsync(file.FileName));
        Assert.Equal("private, max-age=31536000, immutable", http.Response.Headers.CacheControl.ToString());
        Assert.False(string.IsNullOrEmpty(http.Response.Headers.ETag.ToString()));

        var head = new DefaultHttpContext();
        head.Request.Method = HttpMethods.Head;
        head.Request.Headers.IfNoneMatch = http.Response.Headers.ETag.ToString();
        var notModified = await WallGeometrySplatEndpoints.HandleAsync(
            h.WallId, modelId, null, null, new ClaimsPrincipal(), head, h.WallService, h.CurrentUser,
            h.DbContextFactory, Kiosk(null), s.Files, CancellationToken.None);
        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsType<StatusCodeHttpResult>(notModified).StatusCode);
    }

    [Fact]
    public async Task Anonymous_OnlyTheShareTokenAndTheWallsOwnKioskGetIn()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var modelId = await ComputeWithSplatAsync(h, s);
        await SetShareTokenAsync(h, "share-me");
        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromException<User>(new UnauthorizedAccessException()));

        Assert.IsType<NotFound>((await ServeAsync(h, s, h.WallId, modelId, null, Kiosk(null))).Result);
        Assert.IsType<NotFound>((await ServeAsync(h, s, h.WallId, modelId, "wrong", Kiosk(null))).Result);
        Assert.IsType<NotFound>((await ServeAsync(h, s, h.WallId, modelId, null, Kiosk(Guid.NewGuid()))).Result);
        Assert.IsType<PhysicalFileHttpResult>((await ServeAsync(h, s, h.WallId, modelId, "share-me", Kiosk(null))).Result);
        Assert.IsType<PhysicalFileHttpResult>((await ServeAsync(h, s, h.WallId, modelId, null, Kiosk(h.WallId))).Result);
    }

    [Fact]
    public async Task Stranger_AnotherWallsUrl_AndAnUnknownModel_GetNotFound()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var modelId = await ComputeWithSplatAsync(h, s);

        Assert.IsType<NotFound>((await ServeAsync(h, s, Guid.NewGuid(), modelId, null, Kiosk(null))).Result);
        Assert.IsType<NotFound>((await ServeAsync(h, s, h.WallId, Guid.NewGuid(), null, Kiosk(null))).Result);

        // A member of ANOTHER wall asking for this wall's model through their own wall's URL.
        Guid otherWallId;
        await using (var db = h.CreateContext())
        {
            var other = new Wall { Name = "Other", OwnerId = h.Owner.Id };
            db.Walls.Add(other);
            db.WallMembers.Add(new WallMember { WallId = other.Id, UserId = h.Owner.Id, Role = WallRole.Admin });
            await db.SaveChangesAsync();
            otherWallId = other.Id;
        }

        Assert.IsType<NotFound>((await ServeAsync(h, s, otherWallId, modelId, null, Kiosk(null))).Result);

        await using (var db = h.CreateContext())
        {
            db.Users.Add(new User { Identifier = "stranger@test", DisplayName = "Stranger" });
            await db.SaveChangesAsync();
            h.ActingUser = await db.Users.SingleAsync(u => u.Identifier == "stranger@test");
        }

        Assert.IsType<NotFound>((await ServeAsync(h, s, h.WallId, modelId, null, Kiosk(null))).Result);
    }

    [Fact]
    public async Task View_CarriesTexturesAndTheSplat_WithTheShareTokenInEveryUrl()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var modelId = await ComputeWithSplatAsync(h, s);

        var member = (await ViewService(h).BuildAsync(h.WallId, null)).View!;
        Assert.Equal(["0", "5a"], member.Textures.Select(t => t.FacetId));
        Assert.Equal($"/api/walls/{h.WallId}/geometry/{modelId}/textures/0", member.Textures[0].Url);
        Assert.Equal(new(-100, 3000, -100, 2500), member.Textures[0].Bounds);
        Assert.Equal($"/api/walls/{h.WallId}/geometry/{modelId}/splat", member.SplatUrl);
        Assert.Equal(new double[] { 1000, 0, 0, 0, 0, 1000, 0, 0, 0, 0, 1000, 0, 10, 20, 30, 1 }, member.SplatMatrix!.ToArray());

        await SetShareTokenAsync(h, "tok en");
        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromException<User>(new UnauthorizedAccessException()));
        var shared = (await ViewService(h).BuildAsync(h.WallId, null, "tok en")).View!;
        Assert.All(shared.Textures, t => Assert.EndsWith("?token=tok%20en", t.Url));
        Assert.Equal($"/api/walls/{h.WallId}/geometry/{modelId}/splat?token=tok%20en", shared.SplatUrl);
    }

    [Fact]
    public async Task BigScene_GetsALevelOfDetailLadder_InTheViewAndOnTheRoute()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.Spz = SpzDecimatorTests.Spz(330_000, shDegree: 0);
        var modelId = await ComputeWithSplatAsync(h, s);

        var view = (await ViewService(h).BuildAsync(h.WallId, null)).View!;
        Assert.Equal([40_000, 120_000, 250_000, 330_000], view.SplatLevels.Select(l => l.Splats));
        Assert.Equal($"/api/walls/{h.WallId}/geometry/{modelId}/splat?lod=120000", view.SplatLevels[1].Url);
        Assert.Equal(view.SplatUrl, view.SplatLevels[^1].Url);
        Assert.Null(view.SplatMobileUrl);                    // the ladder supersedes the mobile copy

        var (full, fullHttp) = await ServeAsync(h, s, h.WallId, modelId, null, Kiosk(null));
        var (level, levelHttp) = await ServeAsync(h, s, h.WallId, modelId, null, Kiosk(null), "120000");
        var (unknown, _) = await ServeAsync(h, s, h.WallId, modelId, null, Kiosk(null), "77");
        var levelBytes = await File.ReadAllBytesAsync(Assert.IsType<PhysicalFileHttpResult>(level).FileName);
        Assert.Equal(s.SplatClient.Spz, await File.ReadAllBytesAsync(Assert.IsType<PhysicalFileHttpResult>(full).FileName));
        Assert.Equal(s.SplatClient.Spz, await File.ReadAllBytesAsync(Assert.IsType<PhysicalFileHttpResult>(unknown).FileName));
        Assert.Equal(120_000, SpzDecimator.CountOf(levelBytes));
        Assert.Equal(view.SplatLevels[1].SizeBytes, levelBytes.LongLength);
        Assert.NotEqual(fullHttp.Response.Headers.ETag.ToString(), levelHttp.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task SmallOrUnreadableScene_HasNoMobileLevelOfDetail_AndTheRouteFallsBackToTheFullOne()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var modelId = await ComputeWithSplatAsync(h, s);     // the fake's .spz is not a real scene

        var view = (await ViewService(h).BuildAsync(h.WallId, null)).View!;
        Assert.Null(view.SplatMobileUrl);
        Assert.Equal(view.SplatUrl, Assert.Single(view.SplatLevels).Url);
        var (mobile, _) = await ServeAsync(h, s, h.WallId, modelId, null, Kiosk(null), "mobile");
        Assert.Equal(s.SplatClient.Spz, await File.ReadAllBytesAsync(Assert.IsType<PhysicalFileHttpResult>(mobile).FileName));
    }

    [Fact]
    public async Task View_WithoutASplat_OrForAnInactiveModel_HasNone()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var view = (await ViewService(h).BuildAsync(h.WallId, null)).View!;
        Assert.Equal(2, view.Textures.Count);
        Assert.Null(view.SplatUrl);
        Assert.Null(view.SplatMatrix);

        await using (var db = h.CreateContext())
        {
            var model = await db.WallGeometryModels.SingleAsync();
            db.WallGeometrySplats.Add(new WallGeometrySplat
            {
                GeometryModelId = model.Id, StoredPath = "x.spz", FrameJson = FakeComputeJobClient.SplatFrame(true),
            });
            db.WallGeometryModels.Add(new WallGeometryModel
            {
                WallId = h.WallId, Json = model.Json, SchemaVersion = 1, Source = "newer", IsActive = true,
            });
            model.IsActive = false;
            await db.SaveChangesAsync();
        }

        var newer = (await ViewService(h).BuildAsync(h.WallId, null)).View!;
        Assert.Null(newer.SplatUrl);
        Assert.Empty(newer.Textures);
    }

    private static Wall3DViewService ViewService(WallTestHarness h) => new(
        h.WallService, h.CurrentUser, h.DbContextFactory, Wall3DViewAccessTests.Captures(h), NullLogger<Wall3DViewService>.Instance);

    private static async Task<Guid> ComputeWithSplatAsync(WallTestHarness h, CaptureScenario s)
    {
        s.SplatClient.IsConfigured = true;
        var captureId = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        await using var db = h.CreateContext();
        return (await db.WallGeometrySplats.SingleAsync()).GeometryModelId;
    }

    private static async Task SetShareTokenAsync(WallTestHarness h, string token)
    {
        await using var db = h.CreateContext();
        (await db.Walls.SingleAsync(w => w.Id == h.WallId)).ShareToken = token;
        await db.SaveChangesAsync();
    }

    private static async Task<(IResult Result, HttpContext Http)> ServeAsync(
        WallTestHarness h, CaptureScenario s, Guid wallId, Guid modelId, string? token, IKioskContext kiosk, string? lod = null)
    {
        var http = new DefaultHttpContext();
        var result = await WallGeometrySplatEndpoints.HandleAsync(
            wallId, modelId, token, lod, new ClaimsPrincipal(), http, h.WallService, h.CurrentUser,
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
