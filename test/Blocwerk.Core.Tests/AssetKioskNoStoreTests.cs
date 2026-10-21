// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets.Manifest;
using Blocwerk.Core.Assets.Resolution;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A kiosk tablet's caches are shared by every identity on it (one <c>k-</c> partition). So a kiosk session, act-as
/// included, gets every kind outside the manifest's Kiosk class (video thumbnails) as <c>no-store</c>, without a
/// validator, while the kinds the Kiosk class lists keep the ordinary revalidating policy, and a member off the tablet
/// keeps the revalidating thumbnail.
/// </summary>
public class AssetKioskNoStoreTests
{
    private const string Revalidated = "private, no-cache";

    [Fact]
    public async Task AnActAsMember_GetsAThumbnail_AsNoStore_WithoutAValidator()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        f.BindKiosk(f.Harness.WallId);

        foreach (var sha in new[] { s.VideoThumbnail, s.DraftVideoThumbnail })
        {
            var http = await f.GetAsync(f.Harness.WallId, sha, configure: h => h.Request.Headers.IfNoneMatch = $"\"{sha}\"");

            Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
            Assert.Equal(AssetResponse.NoStoreCacheControl, http.Response.Headers.CacheControl.ToString());
            Assert.Equal("Cookie", http.Response.Headers.Vary.ToString());
            Assert.False(http.Response.Headers.ContainsKey("ETag"));
            Assert.NotEmpty(AssetRouteFixture.Body(http));
        }
    }

    [Fact]
    public async Task AnActAsMember_StillGetsTheKioskClassKinds_Revalidating()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        f.BindKiosk(f.Harness.WallId);

        foreach (var sha in new[] { s.Panel, s.Gallery, s.Texture, s.Splat })
        {
            Assert.Equal(Revalidated, (await f.GetAsync(f.Harness.WallId, sha)).Response.Headers.CacheControl.ToString());
        }
    }

    [Fact]
    public async Task AnAnonymousKiosk_StillGetsA404_ForAThumbnail()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        f.Caller = null;
        f.BindKiosk(f.Harness.WallId);

        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.Harness.WallId, s.VideoThumbnail));
    }

    [Fact]
    public async Task AMemberOffTheTablet_GetsTheThumbnail_Revalidating()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();

        var http = await f.GetAsync(f.Harness.WallId, s.VideoThumbnail);

        Assert.Equal(Revalidated, http.Response.Headers.CacheControl.ToString());
        Assert.Equal($"\"{s.VideoThumbnail}\"", http.Response.Headers.ETag.ToString());
    }

    /// <summary>The route and the manifest share one definition of the Kiosk class, so they cannot drift.</summary>
    [Fact]
    public void TheKioskClass_LeavesOutVideoThumbnailsOnly()
    {
        var left = Enum.GetValues<AssetKind>().Where(k => !ManifestAudiences.Lists(ManifestAudience.Kiosk, k));

        Assert.Equal([AssetKind.VideoThumbnail], left);
        Assert.All(Enum.GetValues<AssetKind>(), k => Assert.True(ManifestAudiences.Lists(ManifestAudience.Member, k)));
    }
}
