// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using System.Text.Json;
using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.DataDocs;
using Blocwerk.Web.Endpoints.Feed;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The data documents (Phase 3 plan §1): the <c>wall</c>, <c>holds</c>, <c>boulders</c>, <c>gallery</c> and
/// <c>comments</c> kinds of <see cref="DocumentEndpoints.Route"/>, and their index <see cref="DocumentEndpoints.IndexRoute"/>.
/// Both decide the caller's class with <see cref="DocumentAudienceGate"/> (the feed's grant, kiosk first); a document is
/// served only from the caller's own class.
/// </summary>
/// <remarks>
/// The index is per caller and per moment: <c>private, no-cache</c>, a strong ETag over its body, and
/// <c>Vary: Cookie</c>. Its refusals are the feed's (401 <c>unauthenticated</c>, 403 <c>no-access</c> for a non-member
/// with no or a stale share token, 410 <c>account-gone</c>, each <c>no-store</c>), and an unknown wall is a 404. A
/// document refusal is the route's bare 404, like every other <c>/d/</c> refusal, and a kiosk session that may not cache
/// its wall gets the document <c>no-store</c> (<see cref="DocumentCaching.NoStore"/>), as on the view3d and <c>/a/</c> routes.
/// </remarks>
internal static class DataDocumentEndpoints
{
    /// <summary><c>GET /api/walls/{w}/docs[?token=]</c>.</summary>
    public static async Task<IResult> HandleIndexAsync(
        Guid wallId,
        [FromQuery] string? token,
        HttpContext http,
        [FromServices] DataDocumentService documents,
        CancellationToken ct)
    {
        var access = await documents.AccessAsync(wallId, token, FeedRequestAccess.SessionOf(http), ct);
        if (access.Audience is not { } audience)
        {
            return Refused(http, access);
        }

        if (await documents.IndexAsync(wallId, audience, ct) is not { } index)
        {
            FeedRequestAccess.NoStore(http);
            return Results.NotFound();
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(index, DataDocumentSchema.Options);
        var etag = $"\"{Convert.ToHexStringLower(SHA256.HashData(body))}\"";
        http.Response.Headers.CacheControl = AssetResponse.RevalidateCacheControl;
        http.Response.Headers.ETag = etag;
        http.Response.Headers.Vary = "Cookie";
        if (ImageResponse.Matches(http.Request, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Bytes(body, "application/json");
    }

    /// <summary>One data document; the caller's class must be the one it was built for.</summary>
    public static async Task<IResult> HandleDocumentAsync(Guid wallId, string kind, string sha, string? token, HttpContext http, CancellationToken ct)
    {
        var documents = http.RequestServices.GetRequiredService<DataDocumentService>();
        var access = await documents.AccessAsync(wallId, token, FeedRequestAccess.SessionOf(http), ct);
        if (access.Audience is not { } audience || await documents.GetAsync(wallId, audience, kind, sha, ct) is not { } document)
        {
            return Results.NotFound();
        }

        var noStore = DocumentCaching.NoStore(http.RequestServices.GetService<IKioskContext>(), wallId);
        return DocumentResponse.Write(http, document.Sha, document.Json, document.Brotli, noStore);
    }

    private static IResult Refused(HttpContext http, DocumentAccess access)
    {
        if (access.WallMissing)
        {
            FeedRequestAccess.NoStore(http);
            return Results.NotFound();
        }

        return FeedRequestAccess.Refused(http, access.Refusal);
    }
}
