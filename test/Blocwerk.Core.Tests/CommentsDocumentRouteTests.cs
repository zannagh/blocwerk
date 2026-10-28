// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Contracts.Documents;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>/d/comments/{sha}</c> over HTTP: served under the caller's own class only (a members' sha is a share link's 404),
/// never to an API key, and <c>no-store</c> to a kiosk the cache rules refuse, as the 3.1 documents are.
/// </summary>
public sealed class CommentsDocumentRouteTests
{
    [Fact]
    public async Task TheMembersSha_IsServedToAMember_AndIs404ForAShareLink_WhichGetsItsOwn()
    {
        using var f = await SeededAsync();
        var seed = await CommentsDocumentSeed.AddAsync(f);
        f.Caller = f.Member;
        var members = DataDocumentFixture.Sha(await f.IndexAsync(), DataDocumentKinds.Comments);

        var http = await f.DocumentHttpAsync(DataDocumentKinds.Comments, members);
        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal($"\"{members}\"", http.Response.Headers.ETag.ToString());
        var document = JsonSerializer.Deserialize<CommentsDocument>(((MemoryStream)http.Response.Body).ToArray(), DataDocumentSchema.Options)!;
        Assert.Contains(document.Comments, c => c.Id == seed.OnDraft);

        f.Caller = null;
        var shared = await f.DocumentHttpAsync(DataDocumentKinds.Comments, members, DataDocumentFixture.ShareToken);
        Assert.Equal(StatusCodes.Status404NotFound, shared.Response.StatusCode);
        Assert.True(string.IsNullOrEmpty(shared.Response.Headers.CacheControl.ToString()));
        var own = DataDocumentFixture.Sha(await f.IndexAsync(DataDocumentFixture.ShareToken), DataDocumentKinds.Comments);
        Assert.NotEqual(members, own);
        Assert.Equal(StatusCodes.Status200OK, (await f.DocumentHttpAsync(DataDocumentKinds.Comments, own, DataDocumentFixture.ShareToken)).Response.StatusCode);
    }

    [Fact]
    public async Task AnApiKey_NeverReadsTheComments()
    {
        using var f = await SeededAsync();
        await CommentsDocumentSeed.AddAsync(f);
        var sha = DataDocumentFixture.Sha(await f.IndexAsync(), DataDocumentKinds.Comments);
        f.Principal = FeedRouteFixture.ApiKey();

        Assert.Equal(StatusCodes.Status404NotFound, (await f.IndexHttpAsync()).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await f.DocumentHttpAsync(DataDocumentKinds.Comments, sha)).Response.StatusCode);
    }

    [Fact]
    public async Task AKioskTheCacheRulesRefuse_GetsTheCommentsNoStore()
    {
        using var f = await SeededAsync();
        await CommentsDocumentSeed.AddAsync(f);
        f.Caller = null;
        f.BindKiosk(f.WallId);
        var sha = DataDocumentFixture.Sha(await f.IndexAsync(), DataDocumentKinds.Comments);
        f.Kiosk.KioskApiKeyId.Returns(f.KioskKeyId, (Guid?)null);

        var http = await f.DocumentHttpAsync(DataDocumentKinds.Comments, sha);

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    private static async Task<DataDocumentFixture> SeededAsync()
    {
        var f = new DataDocumentFixture();
        await f.SeedAsync();
        return f;
    }
}
