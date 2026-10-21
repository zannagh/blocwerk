// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets;
using Blocwerk.Core.Entities;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// What the <c>/a/</c> routes tell caches: <c>private, no-cache</c> under <c>ETag: "{sha}"</c>, never <c>immutable</c>
/// (the browser's HTTP cache is shared by every identity on the device, and WebKit ignores Clear-Site-Data, so every
/// reuse must revalidate through the access gate), only ever with bytes that hash to that sha, 304 for a holder who may
/// still see it, Range only for splats. And the avatar, which stays off that path on purpose.
/// </summary>
public class AssetRouteCachingTests
{
    private const string Revalidated = "private, no-cache";

    [Fact]
    public async Task AnOriginal_Revalidates_UnderItsSha()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();

        var http = await f.GetAsync(f.Harness.WallId, s.Panel);

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal(Revalidated, http.Response.Headers.CacheControl.ToString());
        Assert.Equal($"\"{s.Panel}\"", http.Response.Headers.ETag.ToString());
        Assert.Equal("image/jpeg", http.Response.ContentType);
        Assert.Equal(f.PanelBytes, AssetRouteFixture.Body(http));
    }

    [Fact]
    public async Task EveryKind_IsServedRevalidating_WithBytesThatHashToTheUrl()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();

        foreach (var sha in new[] { s.WallPhoto, s.ResetPhoto, s.Gallery, s.Texture, s.Mask, s.Splat, s.SplatLod, s.VideoThumbnail })
        {
            var http = await f.GetAsync(f.Harness.WallId, sha);
            Assert.True(http.Response.StatusCode == StatusCodes.Status200OK, $"{sha}: {http.Response.StatusCode}");
            Assert.Equal(Revalidated, http.Response.Headers.CacheControl.ToString());
            if (http.Response.Body.Length > 0)
            {
                Assert.Equal(sha, AssetRouteFixture.Hex(AssetRouteFixture.Body(http)));
            }
        }
    }

    [Fact]
    public async Task AHolderOfTheSha_Gets304_AndAHead_GetsTheLengthWithoutABody()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();

        var revalidated = await f.GetAsync(f.Harness.WallId, s.Panel, configure: h => h.Request.Headers.IfNoneMatch = $"\"{s.Panel}\"");
        var head = await f.GetAsync(f.Harness.WallId, s.Panel, method: "HEAD");

        Assert.Equal(StatusCodes.Status304NotModified, revalidated.Response.StatusCode);
        Assert.Empty(AssetRouteFixture.Body(revalidated));
        Assert.Equal(f.PanelBytes.Length, head.Response.ContentLength);
        Assert.Empty(AssetRouteFixture.Body(head));
    }

    /// <summary>
    /// The revalidation is the gate: a copy the previous identity left in the HTTP cache is only ever confirmed (304) for
    /// a caller who may still see the asset. Anybody else holding the sha gets the plain refusal.
    /// </summary>
    [Fact]
    public async Task ARevalidationFromSomebodyWhoMayNotSeeIt_IsRefused_NotA304()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        void Holder(HttpContext h) => h.Request.Headers.IfNoneMatch = $"\"{s.Panel}\"";

        f.Caller = new User { Identifier = "nobody@test" };
        AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel, configure: Holder));
        f.Caller = null;
        AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel, configure: Holder));
        AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel, width: 640, configure: h => h.Request.Headers.IfNoneMatch = $"\"{s.Panel}-640\""));
    }

    private static void AssertRefused(DefaultHttpContext http)
    {
        Assert.NotEqual(StatusCodes.Status304NotModified, http.Response.StatusCode);
        AssetRouteAccessTests.AssertRefused(http);
    }

    /// <summary>The worst failure mode: bytes rewritten under a hash that was not moved. Never served, never cached.</summary>
    [Fact]
    public async Task BytesThatNoLongerHashToTheUrl_AreA404_WithoutCacheHeaders()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        await using (var db = f.Harness.CreateContext())
        {
            await db.WallPanels.Where(p => p.Id == s.PanelId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Photo, new byte[] { 1 }));
        }

        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel));
        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel, width: 640));
    }

    [Fact]
    public async Task AVariant_Revalidates_UnderShaAndWidth_AndOnlyOnTheLadder()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();

        var http = await f.GetAsync(f.Harness.WallId, s.Panel, width: 1280);

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal(Revalidated, http.Response.Headers.CacheControl.ToString());
        Assert.Equal($"\"{s.Panel}-1280\"", http.Response.Headers.ETag.ToString());
        using (var decoded = SKBitmap.Decode(AssetRouteFixture.Body(http)))
        {
            Assert.Equal(1280, decoded.Width);
        }

        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel, width: 1000));
        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.Harness.WallId, s.Texture, width: 640));
        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.Harness.WallId, s.Splat, width: 640));
    }

    [Fact]
    public async Task ASplat_HonoursRange_AndNothingElseDoes()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();

        var splat = await f.GetAsync(f.Harness.WallId, s.Splat, configure: h => h.Request.Headers.Range = "bytes=0-1");
        var texture = await f.GetAsync(f.Harness.WallId, s.Texture, configure: h => h.Request.Headers.Range = "bytes=0-0");

        Assert.Equal(StatusCodes.Status206PartialContent, splat.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, texture.Response.StatusCode);
        Assert.Equal(2, AssetRouteFixture.Body(texture).Length);
    }

    /// <summary>The avatar's validator is its hash once it has one, and it stays revalidating (never immutable).</summary>
    [Fact]
    public void TheAvatarEtag_IsItsSha_WithTheLegacyTagUntilItIsHashed()
    {
        var userId = Guid.NewGuid();
        byte[] avatar = [3, 1, 4];

        Assert.Equal($"\"{AssetRouteFixture.Hex(avatar)}\"", UserAvatarEndpoints.AvatarEtag(userId, 3, "image/webp", ContentHash.Of(avatar)));
        Assert.Equal(ImageResponse.Etag(userId, 3, "image/webp"), UserAvatarEndpoints.AvatarEtag(userId, 3, "image/webp", null));
    }

    [Fact]
    public async Task TheAvatarRoute_SendsTheShaEtag_UnderNoCache()
    {
        using var f = new AssetRouteFixture();
        await f.SeedAsync();
        byte[] avatar = [3, 1, 4, 1, 5];
        await using (var db = f.Harness.CreateContext())
        {
            var owner = await db.Users.SingleAsync(u => u.Id == f.Harness.Owner.Id);
            owner.AvatarImage = avatar;
            owner.AvatarContentType = "image/webp";
            await db.SaveChangesAsync();
        }

        var http = await AvatarRoute.GetAsync(f.Harness, f.Harness.Owner.Id);

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal(ImageResponse.MutableCacheControl, http.Response.Headers.CacheControl.ToString());
        Assert.Equal($"\"{AssetRouteFixture.Hex(avatar)}\"", http.Response.Headers.ETag.ToString());
    }
}
