// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Security.Claims;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Entities;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>GET /api/walls/{w}/d/view3d/{sha}</c>: the <c>/a/</c> routes' grants and refusals, their revalidating caching, the kiosk
/// no-store rule, Brotli, and the rebuild after a restart that never serves bytes under a sha they do not hash to.
/// </summary>
public class View3DDocumentRouteTests
{
    [Fact]
    public async Task AMember_GetsTheDocument_Revalidating_UnderItsSha()
    {
        using var f = new View3DDocumentFixture();
        var sha = await f.SeedAsync();

        var http = await f.GetAsync(f.WallId, sha);

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal(AssetResponse.RevalidateCacheControl, http.Response.Headers.CacheControl.ToString());
        Assert.Equal($"\"{sha}\"", http.Response.Headers.ETag.ToString());
        Assert.Equal("application/json", http.Response.ContentType);
        Assert.Equal(sha, ContentHash.ToHex(ContentHash.Of(AssetRouteFixture.Body(http))));
    }

    [Fact]
    public async Task Revalidation_Is304_AndBrotliIsTheSameBytes()
    {
        using var f = new View3DDocumentFixture();
        var sha = await f.SeedAsync();

        var again = await f.GetAsync(f.WallId, sha, configure: h => h.Request.Headers.IfNoneMatch = $"\"{sha}\"");
        var br = await f.GetAsync(f.WallId, sha, configure: h => h.Request.Headers.AcceptEncoding = "gzip, br");
        var head = await f.GetAsync(f.WallId, sha, method: "HEAD");

        Assert.Equal(StatusCodes.Status304NotModified, again.Response.StatusCode);
        Assert.Equal("br", br.Response.Headers.ContentEncoding.ToString());
        using var inflated = new MemoryStream();
        await using (var brotli = new BrotliStream(new MemoryStream(AssetRouteFixture.Body(br)), CompressionMode.Decompress))
        {
            await brotli.CopyToAsync(inflated);
        }

        Assert.Equal(sha, ContentHash.ToHex(ContentHash.Of(inflated.ToArray())));
        Assert.Empty(AssetRouteFixture.Body(head));
        Assert.True(head.Response.ContentLength > 0);
    }

    [Fact]
    public async Task EveryRefusal_IsTheSameBare404()
    {
        using var f = new View3DDocumentFixture();
        var sha = await f.SeedAsync();
        var other = f.Store.Publish(View3DDocumentFixture.ViewOf(f.Assets.Seeded.OtherWallId)).Sha;

        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.WallId, sha, kind: "wall"));
        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.WallId, sha.ToUpperInvariant()));
        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.WallId, other));
        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.Assets.Seeded.OtherWallId, other));
        f.Assets.Caller = new User { Identifier = "nobody@test" };
        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.WallId, sha));
        f.Assets.Caller = null;
        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.WallId, sha));
        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.WallId, sha, query: "?token=rotated"));
    }

    [Fact]
    public async Task AShareToken_Grants_AnonymouslyAndToASignedInNonMember()
    {
        using var f = new View3DDocumentFixture();
        var sha = await f.SeedAsync();
        var query = "?token=" + AssetRouteFixture.ShareToken;

        f.Assets.Caller = null;
        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.WallId, sha, query: query)).Response.StatusCode);
        f.Assets.Caller = new User { Identifier = "nobody@test" };
        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.WallId, sha, query: query)).Response.StatusCode);
    }

    [Fact]
    public async Task ARevalidationFromSomebodyWhoMayNotSeeIt_IsRefused_NotA304()
    {
        using var f = new View3DDocumentFixture();
        var sha = await f.SeedAsync();
        void Revalidate(HttpContext http) => http.Request.Headers.IfNoneMatch = $"\"{sha}\"";

        f.Assets.Caller = new User { Identifier = "nobody@test" };
        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.WallId, sha, configure: Revalidate));
        f.Assets.Caller = null;
        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.WallId, sha, configure: Revalidate));
    }

    [Fact]
    public async Task AnApiKeyPrincipal_GetsA404()
    {
        using var f = new View3DDocumentFixture();
        var sha = await f.SeedAsync();

        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.WallId, sha, configure: http =>
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "k")], ApiKeyAuthenticationHandler.SchemeName))));
    }

    [Fact]
    public async Task TheKiosk_GetsItsOwnWallsDocument_NoStoreWhenItMayNotCacheIt()
    {
        using var f = new View3DDocumentFixture();
        var sha = await f.SeedAsync();
        f.Assets.Caller = null;
        f.Assets.BindKiosk(f.WallId);

        var unkeyed = await f.GetAsync(f.WallId, sha, configure: h => h.Request.Headers.IfNoneMatch = $"\"{sha}\"");
        f.Assets.Kiosk.KioskApiKeyId.Returns(Guid.NewGuid());
        var keyed = await f.GetAsync(f.WallId, sha);
        f.Assets.BindKiosk(f.Assets.Seeded.OtherWallId);
        var elsewhere = await f.GetAsync(f.WallId, sha);

        Assert.Equal(StatusCodes.Status200OK, unkeyed.Response.StatusCode);
        Assert.Equal(AssetResponse.NoStoreCacheControl, unkeyed.Response.Headers.CacheControl.ToString());
        Assert.False(unkeyed.Response.Headers.ContainsKey("ETag"));
        Assert.Contains("Cookie", unkeyed.Response.Headers.Vary.ToString(), StringComparison.Ordinal);
        Assert.Equal(AssetResponse.RevalidateCacheControl, keyed.Response.Headers.CacheControl.ToString());
        AssetRouteAccessTests.AssertRefused(elsewhere);
    }

    [Fact]
    public async Task AfterARestart_TheDocumentIsRebuilt_OnlyWhenItHashesToTheSha()
    {
        using var f = new View3DDocumentFixture();
        var sha = await f.SeedAsync();
        f.Restart();

        var rebuilt = await f.GetAsync(f.WallId, sha);
        f.Restart();
        var unknown = await f.GetAsync(f.WallId, new string('e', 64));
        var kept = await f.GetAsync(f.WallId, sha);

        Assert.Equal(StatusCodes.Status200OK, rebuilt.Response.StatusCode);
        Assert.Equal(sha, ContentHash.ToHex(ContentHash.Of(AssetRouteFixture.Body(rebuilt))));
        AssetRouteAccessTests.AssertRefused(unknown);
        Assert.Equal(StatusCodes.Status200OK, kept.Response.StatusCode); // the failed rebuild stored the current document
        await f.Views.Received(2).BuildAsync(f.WallId, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RandomShasAndRandomBoulderIds_CostOneRebuildPerWallPerWindow()
    {
        using var f = new View3DDocumentFixture();
        await f.SeedAsync();
        f.Restart();
        var boulderId = Guid.NewGuid();

        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.WallId, new string('9', 64), query: $"?boulder={boulderId}"));
        for (var i = 0; i < 5; i++)
        {
            AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.WallId, new string((char)('0' + i), 64), query: $"?boulder={Guid.NewGuid()}"));
            AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.WallId, new string((char)('a' + i), 64)));
        }

        await f.Views.Received(1).BuildAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await f.Views.Received(1).BuildAsync(f.WallId, boulderId, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConcurrentMisses_ShareOneRebuild()
    {
        using var f = new View3DDocumentFixture();
        var sha = await f.SeedAsync();
        f.Restart();
        var release = new TaskCompletionSource();
        f.Views.BuildAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                await release.Task;
                return new Blocwerk.Core.Geometry.View3D.Wall3DViewResult(
                    Blocwerk.Core.Geometry.View3D.Wall3DViewStatus.Ok, "W", View3DDocumentFixture.ViewOf(c.ArgAt<Guid>(0)));
            });

        var first = Task.Run(() => f.GetAsync(f.WallId, sha));
        for (var i = 0; i < 500 && !f.Views.ReceivedCalls().Any(); i++)
        {
            await Task.Delay(10);
        }

        var second = await f.GetAsync(f.WallId, sha);
        release.SetResult();

        Assert.Equal(StatusCodes.Status200OK, (await first).Response.StatusCode);
        AssetRouteAccessTests.AssertRefused(second); // the build was still running: no second one
        await f.Views.Received(1).BuildAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A304_ComesOnlyAfterTheGate_AndOnlyForAKnownDocument()
    {
        using var f = new View3DDocumentFixture();
        var sha = await f.SeedAsync();
        void Revalidate(HttpContext http) => http.Request.Headers.IfNoneMatch = $"\"{sha}\"";
        f.Restart();

        var member = await f.GetAsync(f.WallId, sha, configure: Revalidate);
        var unknown = await f.GetAsync(f.WallId, new string('e', 64), configure: h => h.Request.Headers.IfNoneMatch = $"\"{new string('e', 64)}\"");
        f.Assets.Caller = null;
        var anonymous = await f.GetAsync(f.WallId, sha, configure: Revalidate);

        Assert.Equal(StatusCodes.Status304NotModified, member.Response.StatusCode);
        Assert.Equal(AssetResponse.RevalidateCacheControl, member.Response.Headers.CacheControl.ToString());
        AssetRouteAccessTests.AssertRefused(unknown);
        AssetRouteAccessTests.AssertRefused(anonymous);
    }

    [Fact]
    public async Task TheBrotliLength_IsThePublishedSize()
    {
        using var f = new View3DDocumentFixture();
        await f.SeedAsync();
        var published = f.Store.Publish(View3DDocumentFixture.ViewOf(f.WallId));

        var br = await f.GetAsync(f.WallId, published.Sha, configure: h => h.Request.Headers.AcceptEncoding = "br");

        Assert.Equal(published.Bytes, AssetRouteFixture.Body(br).LongLength);
    }
}
