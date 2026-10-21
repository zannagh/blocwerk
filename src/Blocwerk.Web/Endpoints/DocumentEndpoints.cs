// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Assets.Documents;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The content-addressed working-data documents (design §6.5): today only <c>view3d</c>, the wall-level model the 3D view
/// draws. Named by the SHA-256 of their bytes and held like the <c>/a/</c> assets: <c>private, no-cache</c> under the strong
/// <c>ETag: "{sha}"</c> (every HTTP-cache reuse revalidates, and the 304 comes only after the gate, so a copy one identity
/// left in the browser's HTTP cache never answers the next), Brotli when the browser accepts it. The service worker still
/// stores it, as it stores any 200 without <c>no-store</c> whose ETag names the sha.
/// </summary>
/// <remarks>
/// Same posture as <see cref="AssetEndpoints"/>: the gallery-image policy, API-key principals rejected, the grant decided by
/// <c>AssetAccessGate</c> through <see cref="View3DDocumentService"/>, and every refusal (an unknown kind, a malformed or
/// unknown sha, another wall's document) the same bare 404 with no cache headers. A kiosk session that may not cache its
/// wall gets <c>no-store</c> and <c>Vary: Cookie</c>, as on the <c>/a/</c> routes.
/// </remarks>
public static class DocumentEndpoints
{
    /// <summary>A wall's working-data document; <c>?token=</c> for a share link, <c>?boulder=</c> as a rebuild hint.</summary>
    public const string Route = "/api/walls/{wallId:guid}/d/{kind}/{sha}";

    /// <summary>
    /// The policy of a served document: the <c>/a/</c> routes' revalidating one (<c>fix/asset-http-cache-isolation</c>'s
    /// <c>AssetResponse.RevalidateCacheControl</c>, which this constant joins once that branch is on staging).
    /// </summary>
    public const string CacheControl = "private, no-cache";

    private static readonly string[] Verbs = [HttpMethods.Get, HttpMethods.Head];

    public static void MapWallDocuments(this WebApplication app)
    {
        app.MapMethods(Route, Verbs, HandleAsync)
            .RequireAuthorization(BlocwerkPolicies.WallGalleryImage)
            .DenyApiKeyPrincipals();
    }

    internal static async Task<IResult> HandleAsync(
        Guid wallId,
        string kind,
        string sha,
        [FromQuery] string? token,
        [FromQuery] Guid? boulder,
        ClaimsPrincipal user,
        HttpContext http,
        [FromServices] View3DDocumentService documents,
        CancellationToken ct)
    {
        if (user.IsApiKeyPrincipal() || kind != View3DDocument.Kind
            || await documents.GetAsync(wallId, sha, token, boulder, ct) is not { } served)
        {
            return Results.NotFound();
        }

        var etag = $"\"{served.Document.Sha}\"";
        if (served.NoStore)
        {
            http.Response.Headers.CacheControl = AssetResponse.NoStoreCacheControl;
            http.Response.Headers.Vary = "Cookie, Accept-Encoding";
        }
        else
        {
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = CacheControl;
            http.Response.Headers.Vary = "Accept-Encoding";
            if (ImageResponse.Matches(http.Request, etag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
        }

        var brotli = AcceptsBrotli(http.Request);
        var body = brotli ? served.Document.Brotli : served.Document.Json;
        if (brotli)
        {
            http.Response.Headers.ContentEncoding = "br";
        }

        if (HttpMethods.IsHead(http.Request.Method))
        {
            http.Response.ContentType = "application/json";
            http.Response.ContentLength = body.LongLength;
            return Results.Empty;
        }

        return Results.Bytes(body, "application/json");
    }

    // "br" listed in Accept-Encoding with any weight but q=0.
    private static bool AcceptsBrotli(HttpRequest request) =>
        request.GetTypedHeaders().AcceptEncoding.Any(e =>
            e.Value.Equals("br", StringComparison.OrdinalIgnoreCase) && (e.Quality ?? 1) > 0);
}
