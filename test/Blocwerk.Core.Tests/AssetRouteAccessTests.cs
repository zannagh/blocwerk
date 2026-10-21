// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The auth matrix of <c>/api/walls/{w}/a/{sha}</c>, through the real registered endpoint: the grants are the panel
/// photo route's (member, matching share token, the wall's own anonymous kiosk) and every refusal is a bare 404.
/// </summary>
public class AssetRouteAccessTests
{
    [Fact]
    public async Task AMember_GetsTheBytes()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();

        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.Harness.WallId, s.Panel)).Response.StatusCode);
    }

    [Fact]
    public async Task ASignedInNonMember_GetsA404()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        f.Caller = new User { Identifier = "nobody@test" };

        AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel));
    }

    [Fact]
    public async Task AnAnonymousCaller_OnAPrivateWall_GetsA404()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        f.Caller = null;

        AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel));
        AssertRefused(await f.GetAsync(s.OtherWallId, s.OtherWallPanel));
        AssertRefused(await f.GetAsync(s.OtherWallId, s.OtherWallPanel, token: AssetRouteFixture.ShareToken));
    }

    [Fact]
    public async Task AValidShareToken_Grants_AnonymouslyAndToASignedInNonMember()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();

        f.Caller = null;
        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.Harness.WallId, s.Panel, token: AssetRouteFixture.ShareToken)).Response.StatusCode);
        f.Caller = new User { Identifier = "nobody@test" };
        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.Harness.WallId, s.Panel, token: AssetRouteFixture.ShareToken)).Response.StatusCode);
    }

    /// <summary>Rotating the token locks out the old link at once; a member holding it still falls through to membership.</summary>
    [Fact]
    public async Task ARotatedToken_IsRefusedToAnAnonymousCaller_ButNotToAMember()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        await using (var db = f.Harness.CreateContext())
        {
            await db.Walls.Where(w => w.Id == f.Harness.WallId).ExecuteUpdateAsync(u => u.SetProperty(w => w.ShareToken, "rotated"));
        }

        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.Harness.WallId, s.Panel, token: AssetRouteFixture.ShareToken)).Response.StatusCode);
        f.Caller = null;
        AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel, token: AssetRouteFixture.ShareToken));
        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.Harness.WallId, s.Panel, token: "rotated")).Response.StatusCode);
    }

    [Fact]
    public async Task AnAnonymousKiosk_SeesItsOwnWall_AndNoOther()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        f.Caller = null;
        f.BindKiosk(f.Harness.WallId);

        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.Harness.WallId, s.Panel)).Response.StatusCode);
        AssertRefused(await f.GetAsync(s.OtherWallId, s.OtherWallPanel));
    }

    /// <summary>A kiosk never reaches another wall: not with that wall's share token, not as a member acting there.</summary>
    [Fact]
    public async Task AKiosk_IsPinnedToItsWall_ThroughATokenAndThroughActAs()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        await using (var db = f.Harness.CreateContext())
        {
            db.WallMembers.Add(new WallMember { WallId = s.OtherWallId, UserId = f.Harness.Owner.Id, Role = WallRole.Member });
            await db.Walls.Where(w => w.Id == s.OtherWallId).ExecuteUpdateAsync(u => u.SetProperty(w => w.ShareToken, "other-token"));
            await db.SaveChangesAsync();
        }

        f.BindKiosk(f.Harness.WallId);
        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.Harness.WallId, s.Panel)).Response.StatusCode);
        AssertRefused(await f.GetAsync(s.OtherWallId, s.OtherWallPanel));
        f.Caller = null;
        AssertRefused(await f.GetAsync(s.OtherWallId, s.OtherWallPanel, token: "other-token"));
    }

    /// <summary>The legacy thumbnail route's rules: no draft boulder's clip under a share link, none to an anonymous kiosk.</summary>
    [Fact]
    public async Task VideoThumbnails_FollowTheLegacyRoute()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.Harness.WallId, s.DraftVideoThumbnail)).Response.StatusCode);

        f.Caller = null;
        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.Harness.WallId, s.VideoThumbnail, token: AssetRouteFixture.ShareToken)).Response.StatusCode);
        AssertRefused(await f.GetAsync(f.Harness.WallId, s.DraftVideoThumbnail, token: AssetRouteFixture.ShareToken));

        f.BindKiosk(f.Harness.WallId);
        AssertRefused(await f.GetAsync(f.Harness.WallId, s.VideoThumbnail));
        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.Harness.WallId, s.Panel)).Response.StatusCode);
    }

    [Fact]
    public async Task AnApiKeyPrincipal_GetsA404_OnBothRoutes()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        void AsApiKey(HttpContext http) =>
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "k")], ApiKeyAuthenticationHandler.SchemeName));

        AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel, configure: AsApiKey));
        AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel, width: 640, configure: AsApiKey));
    }

    /// <summary>A sha the wall used to hold, one of another wall, or none at all: the same 404, and nothing to cache.</summary>
    [Fact]
    public async Task AStaleOrForeignSha_GetsA404_WithoutCacheHeaders()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        await using (var db = f.Harness.CreateContext())
        {
            await db.WallPanels.Where(p => p.Id == s.PanelId).ExecuteUpdateAsync(u => u.SetProperty(p => p.PhotoSha256, new byte[32]));
        }

        AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel));
        AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel, width: 1280));
        AssertRefused(await f.GetAsync(f.Harness.WallId, s.OtherWallPanel));
        AssertRefused(await f.GetAsync(f.Harness.WallId, new string('f', 64)));
    }

    internal static void AssertRefused(HttpContext http)
    {
        Assert.Equal(StatusCodes.Status404NotFound, http.Response.StatusCode);
        Assert.False(http.Response.Headers.ContainsKey("Cache-Control"), "a refusal must not be cacheable");
        Assert.False(http.Response.Headers.ContainsKey("ETag"));
        Assert.Empty(AssetRouteFixture.Body(http));
    }
}
