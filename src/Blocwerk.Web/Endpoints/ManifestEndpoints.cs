// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using System.Text.Json;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Contracts.Assets;
using Blocwerk.Core.Assets.Manifest;
using Blocwerk.Core.Assets.Partition;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The asset cache's control routes (design §6.3): a wall's manifest, the caller's cache partition, and the caller's
/// cache summary (the count behind the high-storage banner).
/// </summary>
/// <remarks>
/// <para>
/// The manifest takes the posture of the <c>/a/</c> routes it lists: the gallery-image policy, API-key principals
/// rejected, and the grant decided by <c>AssetAccessGate</c>. Every refusal is the same bare 404, which a client takes as
/// "drop this wall's assets" (§6.6 rule 4). A 200 is <c>private, no-cache</c> with the wall hash as a WEAK ETag (the
/// body's cursor is outside the hash, so two bodies under one tag are equivalent, not byte-identical), so a client
/// revalidates every time and an unchanged wall costs a 304.
/// </para>
/// <para>
/// The partition and summary routes answer anyone (an anonymous caller is <c>anon</c> with no 3D walls) and are never
/// stored by any cache. They sit outside the API-key prefixes, so a bearer presented there resolves nobody.
/// </para>
/// </remarks>
public static class ManifestEndpoints
{
    /// <summary>A wall's manifest; <c>?token=</c> for a share link.</summary>
    public const string ManifestRoute = "/api/walls/{wallId:guid}/manifest";

    /// <summary>The caller's cache partition.</summary>
    public const string PartitionRoute = "/api/me/cache-partition";

    /// <summary>What the caller's storage notices need (<see cref="CacheSummaryResponse"/>).</summary>
    public const string SummaryRoute = "/api/me/cache-summary";

    public static void MapAssetManifest(this WebApplication app)
    {
        app.MapGet(ManifestRoute, HandleManifestAsync)
            .RequireAuthorization(BlocwerkPolicies.WallGalleryImage)
            .DenyApiKeyPrincipals();
        app.MapGet(PartitionRoute, HandlePartitionAsync)
            .AllowAnonymous();
        app.MapGet(SummaryRoute, HandleSummaryAsync)
            .AllowAnonymous();
    }

    internal static async Task<IResult> HandleManifestAsync(
        Guid wallId,
        [FromQuery] string? token,
        ClaimsPrincipal user,
        HttpContext http,
        [FromServices] WallManifestService manifests,
        CancellationToken ct)
    {
        if (user.IsApiKeyPrincipal() || await manifests.GetAsync(wallId, token, ct) is not { } manifest)
        {
            return Results.NotFound();
        }

        var etag = new EntityTagHeaderValue($"\"{manifest.Sha}\"", isWeak: true);
        http.Response.Headers.CacheControl = "private, no-cache";
        http.Response.Headers.ETag = etag.ToString();
        if (http.Request.GetTypedHeaders().IfNoneMatch.Any(e => e.Compare(etag, useStrongComparison: false)))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Text(JsonSerializer.Serialize(manifest, ManifestSchema.Options), "application/json");
    }

    internal static async Task<IResult> HandlePartitionAsync(HttpContext http, [FromServices] ICachePartitionResolver partitions)
    {
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(new CachePartitionResponse(await partitions.ResolveAsync()));
    }

    internal static async Task<IResult> HandleSummaryAsync(HttpContext http, [FromServices] CacheSummaryService summaries, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(await summaries.GetAsync(ct));
    }
}
