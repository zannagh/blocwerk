// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Core.Tests;

/// <summary><c>/api/me/walls/{w}/mine</c> over HTTP (PR 3.4): its shape, <c>no-store</c> on every answer, and the refusals.</summary>
public sealed class MineDocumentRouteTests
{
    [Fact]
    public async Task AMember_Gets200_NoStore_WithCursorTicksAndFavourites()
    {
        using var f = await SeededAsync();

        var http = await f.HttpAsync();

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
        var body = MineDocumentFixture.Body(http);
        Assert.Contains("\"cursor\":", body);
        Assert.Contains("\"ticks\":[", body);
        Assert.Contains("\"favourites\":[", body);
        var mine = MineDocumentFixture.Parse(http);
        Assert.Equal(4, mine.Ticks.Count);
        Assert.DoesNotContain(f.Setter.Id.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(f.Member.Id.ToString(), body, StringComparison.OrdinalIgnoreCase); // whose marks: implied, never written
    }

    [Fact]
    public async Task AKiosk_Is403_Nobody401_AGoneAccount410_AKey403_AnUnknownWall404_AllNoStore()
    {
        using var f = await SeededAsync();
        f.Docs.BindKiosk(f.WallId);
        await AssertRefusedAsync(await f.HttpAsync(), StatusCodes.Status403Forbidden, FeedRefusals.NoAccess);

        using var g = await SeededAsync();
        g.Docs.Caller = null;
        await AssertRefusedAsync(await g.HttpAsync(), StatusCodes.Status401Unauthorized, FeedRefusals.Unauthenticated);
        g.Docs.Principal = FeedRouteFixture.SignedIn();
        await AssertRefusedAsync(await g.HttpAsync(), StatusCodes.Status410Gone, FeedRefusals.AccountGone);
        g.Docs.Caller = g.Member;
        g.Docs.Principal = FeedRouteFixture.ApiKey();
        var key = await g.HttpAsync();
        Assert.Equal(StatusCodes.Status404NotFound, key.Response.StatusCode); // the DenyApiKeyPrincipals filter answers first
        Assert.Equal(string.Empty, MineDocumentFixture.Body(key));

        g.Docs.Principal = null;
        var unknown = await g.HttpAsync(Guid.NewGuid());
        Assert.Equal(StatusCodes.Status404NotFound, unknown.Response.StatusCode);
        Assert.Equal("no-store", unknown.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task ANonMember_Is403()
    {
        using var f = await SeededAsync();
        f.Docs.Caller = f.Outsider;

        await AssertRefusedAsync(await f.HttpAsync(), StatusCodes.Status403Forbidden, FeedRefusals.NoAccess);
    }

    private static Task AssertRefusedAsync(HttpContext http, int status, string error)
    {
        Assert.Equal(status, http.Response.StatusCode);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
        Assert.Contains($"\"{error}\"", MineDocumentFixture.Body(http));
        Assert.DoesNotContain("ticks", MineDocumentFixture.Body(http));
        return Task.CompletedTask;
    }

    private static async Task<MineDocumentFixture> SeededAsync()
    {
        var f = new MineDocumentFixture();
        await f.SeedAsync();
        return f;
    }
}
