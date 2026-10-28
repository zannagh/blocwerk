// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Assets.Documents;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The content-addressed working-data documents (design §6.5): <c>view3d</c>, the wall-level model the 3D view draws, and
/// the Phase 3 data documents <c>wall</c>, <c>holds</c>, <c>boulders</c> and <c>gallery</c> with their index
/// (<see cref="DataDocumentEndpoints"/>). Named by the SHA-256 of their bytes and held like the <c>/a/</c> assets
/// (<see cref="DocumentResponse"/>): <c>private, no-cache</c> under the strong <c>ETag: "{sha}"</c> (every HTTP-cache reuse
/// revalidates, and the 304 comes only after the gate, so a copy one identity left in the browser's HTTP cache never
/// answers the next), Brotli when the browser accepts it. The service worker still stores it, as it stores any 200
/// without <c>no-store</c> whose ETag names the sha.
/// </summary>
/// <remarks>
/// Same posture as <see cref="AssetEndpoints"/>: the gallery-image policy, API-key principals rejected, the grant decided by
/// <c>AssetAccessGate</c> through <see cref="View3DDocumentService"/> (the data kinds: <c>DocumentAudienceGate</c>, by
/// class), and every refusal (an unknown kind, a malformed or
/// unknown sha, another wall's document) the same bare 404 with no cache headers. A kiosk session that may not cache its
/// wall gets <c>no-store</c> and <c>Vary: Cookie</c>, as on the <c>/a/</c> routes.
/// </remarks>
public static class DocumentEndpoints
{
    /// <summary>A wall's working-data document; <c>?token=</c> for a share link, <c>?boulder=</c> as a rebuild hint.</summary>
    public const string Route = "/api/walls/{wallId:guid}/d/{kind}/{sha}";

    /// <summary>The caller's data documents of a wall (<see cref="DataDocumentEndpoints"/>); <c>?token=</c> for a share link.</summary>
    public const string IndexRoute = "/api/walls/{wallId:guid}/docs";

    private static readonly string[] Verbs = [HttpMethods.Get, HttpMethods.Head];

    public static void MapWallDocuments(this WebApplication app)
    {
        app.MapMethods(Route, Verbs, HandleAsync)
            .RequireAuthorization(BlocwerkPolicies.WallGalleryImage)
            .DenyApiKeyPrincipals();
        app.MapGet(IndexRoute, DataDocumentEndpoints.HandleIndexAsync)
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
        if (user.IsApiKeyPrincipal())
        {
            return Results.NotFound();
        }

        if (DataDocumentKinds.IsKnown(kind))
        {
            return await DataDocumentEndpoints.HandleDocumentAsync(wallId, kind, sha, token, http, ct);
        }

        if (kind != View3DDocument.Kind || await documents.GetAsync(wallId, sha, token, boulder, ct) is not { } served)
        {
            return Results.NotFound();
        }

        return DocumentResponse.Write(http, served.Document.Sha, served.Document.Json, served.Document.Brotli, served.NoStore);
    }
}
