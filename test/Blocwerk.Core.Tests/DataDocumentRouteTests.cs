// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text.Json;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Feed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>The data-document routes over HTTP: the index's shape and caching, the documents' bytes, and API keys.</summary>
public sealed class DataDocumentRouteTests
{
    [Fact]
    public async Task TheIndex_ListsEveryKind_WithTheOldestCursor_PrivateNoCache_VaryCookie_And304OnItsETag()
    {
        using var f = await SeededAsync();

        var http = await f.IndexHttpAsync();

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal("private, no-cache", http.Response.Headers.CacheControl.ToString());
        Assert.Equal("Cookie", http.Response.Headers.Vary.ToString());
        var index = JsonSerializer.Deserialize<DataDocumentIndex>(((MemoryStream)http.Response.Body).ToArray(), DataDocumentSchema.Options)!;
        Assert.Equal(DataDocumentKinds.All, index.Docs.Select(d => d.Kind));
        Assert.All(index.Docs, d => Assert.Matches("^[0-9a-f]{64}$", d.Sha));
        var oldest = index.Docs.Select(d => FeedCatchUp.Parse(d.Cursor).Position).Min();
        Assert.Equal(oldest, FeedCatchUp.Parse(index.Cursor).Position);

        var etag = http.Response.Headers.ETag.ToString();
        var again = await f.IndexHttpAsync(configure: h => h.Request.Headers.IfNoneMatch = etag);
        Assert.Equal(StatusCodes.Status304NotModified, again.Response.StatusCode);
    }

    [Fact]
    public async Task ADocument_IsServedUnderItsSha_AsBrotliWhenAccepted_AndAnUnknownKindOrShaIs404()
    {
        using var f = await SeededAsync();
        var sha = DataDocumentFixture.Sha(await f.IndexAsync(), DataDocumentKinds.Holds);

        var plain = await f.DocumentHttpAsync(DataDocumentKinds.Holds, sha);
        Assert.Equal($"\"{sha}\"", plain.Response.Headers.ETag.ToString());
        Assert.Equal("private, no-cache", plain.Response.Headers.CacheControl.ToString());
        var json = ((MemoryStream)plain.Response.Body).ToArray();
        Assert.Equal(sha, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(json)));
        Assert.Equal(3, JsonSerializer.Deserialize<HoldsDocument>(json, DataDocumentSchema.Options)!.Holds.Count);

        var brotli = await f.DocumentHttpAsync(DataDocumentKinds.Holds, sha, configure: h => h.Request.Headers.AcceptEncoding = "br");
        Assert.Equal("br", brotli.Response.Headers.ContentEncoding.ToString());
        using var decoded = new MemoryStream();
        await using (var stream = new BrotliStream(new MemoryStream(((MemoryStream)brotli.Response.Body).ToArray()), CompressionMode.Decompress))
        {
            await stream.CopyToAsync(decoded);
        }

        Assert.Equal(json, decoded.ToArray());
        Assert.Equal(StatusCodes.Status404NotFound, (await f.DocumentHttpAsync("mine", sha)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await f.DocumentHttpAsync(DataDocumentKinds.Holds, new string('0', 64))).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await f.DocumentHttpAsync(DataDocumentKinds.Holds, sha.ToUpperInvariant())).Response.StatusCode);
    }

    /// <summary>
    /// A key never reads a document: the gallery policy fails it (the pipeline's 403, as the principal is authenticated),
    /// the route's filter answers 404 if it got that far, and the gate itself refuses it.
    /// </summary>
    [Fact]
    public async Task AnApiKey_IsRefused_ByThePolicy_TheFilter_AndTheGate()
    {
        using var f = await SeededAsync();
        var sha = DataDocumentFixture.Sha(await f.IndexAsync(), DataDocumentKinds.Holds);
        f.Principal = FeedRouteFixture.ApiKey();

        var requirement = new WallGalleryImageRequirement();
        var request = new DefaultHttpContext();
        request.Request.QueryString = new QueryString("?token=" + DataDocumentFixture.ShareToken);
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(request);
        var context = new AuthorizationHandlerContext([requirement], f.Principal, request);
        await new WallGalleryImageHandler(accessor).HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Equal(StatusCodes.Status404NotFound, (await f.IndexHttpAsync()).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await f.DocumentHttpAsync(DataDocumentKinds.Holds, sha)).Response.StatusCode);
        var access = await f.AccessAsync();
        Assert.Null(access.Audience);
        Assert.Equal(FeedAccessRefusal.NoAccess, access.Refusal);
    }

    private static async Task<DataDocumentFixture> SeededAsync()
    {
        var f = new DataDocumentFixture();
        await f.SeedAsync();
        return f;
    }
}
