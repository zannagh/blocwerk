// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using System.Text.Json;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Contracts.Assets;
using Blocwerk.Core.Assets.Manifest;
using Blocwerk.Core.Entities;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>GET /api/walls/{w}/manifest</c> through the real registered endpoint: <c>private, no-cache</c> with the wall hash as
/// its ETag, a 304 for an unchanged wall, and every refusal the <c>/a/</c> routes' bare 404.
/// </summary>
public class ManifestEndpointTests
{
    [Fact]
    public async Task AManifest_IsPrivate_Revalidated_AndTaggedWithTheWallHash()
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();

        var http = await f.GetHttpAsync();
        var body = JsonSerializer.Deserialize<WallManifest>(AssetRouteFixture.Body(http), ManifestSchema.Options)!;

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.StartsWith("application/json", http.Response.ContentType);
        Assert.Equal("private, no-cache", http.Response.Headers.CacheControl.ToString());
        Assert.Equal($"W/\"{body.Sha}\"", http.Response.Headers.ETag.ToString());
        Assert.Equal(body.Sha, ManifestAssembly.WallHash(body));
        Assert.NotNull(body.Cursor);
    }

    /// <summary>The cursor (epoch id, xid positions) serves a member's feed catch-up; nobody else gets it.</summary>
    [Fact]
    public async Task OnlyAMember_GetsTheCursor()
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();
        Assert.NotNull((await f.GetAsync())!.Cursor);

        f.Assets.Caller = null;
        var share = await f.GetHttpAsync(AssetRouteFixture.ShareToken);
        Assert.DoesNotContain("cursor", System.Text.Encoding.UTF8.GetString(AssetRouteFixture.Body(share)));
        f.BindKiosk();
        Assert.Null((await f.GetAsync())!.Cursor);
        f.Assets.Caller = f.Assets.Harness.Owner;
        Assert.Null((await f.GetAsync())!.Cursor); // a kiosk acting as a member is still the kiosk class
    }

    [Fact]
    public async Task AnUnchangedWall_AnswersIfNoneMatch_WithA304()
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();
        var etag = (await f.GetHttpAsync()).Response.Headers.ETag.ToString();

        var http = await f.GetHttpAsync(configure: h => h.Request.Headers.IfNoneMatch = etag);

        Assert.Equal(StatusCodes.Status304NotModified, http.Response.StatusCode);
        Assert.Equal(etag, http.Response.Headers.ETag.ToString());
        Assert.Empty(AssetRouteFixture.Body(http));
        var other = await f.GetHttpAsync(configure: h => h.Request.Headers.IfNoneMatch = "\"other\"");
        Assert.Equal(StatusCodes.Status200OK, other.Response.StatusCode);
    }

    [Fact]
    public async Task Refusals_AreABare404()
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();

        AssetRouteAccessTests.AssertRefused(await f.GetHttpAsync(configure: h =>
            h.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "k")], ApiKeyAuthenticationHandler.SchemeName))));
        f.Assets.Caller = new User { Identifier = "nobody@test" };
        AssetRouteAccessTests.AssertRefused(await f.GetHttpAsync());
        f.BindKiosk(Guid.Empty);
        AssetRouteAccessTests.AssertRefused(await f.GetHttpAsync());
    }
}
