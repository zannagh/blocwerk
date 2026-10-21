// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.Resolution;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The content-addressed asset routes: a wall's stored bytes named by their SHA-256, so the URL can never come to mean
/// other bytes and the response may be held for a year without revalidating (<see cref="AssetResponse"/>).
/// </summary>
/// <remarks>
/// <para>
/// Same posture as the other wall media: the gallery-image policy, API-key principals rejected, and the grant decided
/// per request by <see cref="AssetAccessGate"/> (membership, a matching share token, or the wall's own anonymous
/// kiosk). The sha must belong to the wall in the route: <see cref="IAssetResolver"/> looks it up through the hash
/// columns, scoped by the wall. Every refusal, an unknown or stale sha included, is the same bare 404 with no cache
/// headers, so nothing is cached for it and nothing tells a refused caller that the sha exists elsewhere.
/// </para>
/// <para>
/// A row whose hash is still null (not yet backfilled) is not reachable here at all; its bytes stay on the legacy
/// routes, which keep their URLs, versions and cache policy unchanged.
/// </para>
/// </remarks>
public static class AssetEndpoints
{
    /// <summary>The original bytes (panel, wall photo, gallery, texture, mask, splat, video thumbnail).</summary>
    public const string Route = "/api/walls/{wallId:guid}/a/{sha}";

    /// <summary>A rendition of a photo at a width of <see cref="ImageVariants.Widths"/>; any other width is a 404.</summary>
    public const string VariantRoute = Route + "/{width:int}";

    private static readonly string[] Verbs = [HttpMethods.Get, HttpMethods.Head];

    public static void MapAssets(this WebApplication app)
    {
        app.MapMethods(Route, Verbs, HandleAsync)
            .RequireAuthorization(BlocwerkPolicies.WallGalleryImage)
            .DenyApiKeyPrincipals();
        app.MapMethods(VariantRoute, Verbs, HandleVariantAsync)
            .RequireAuthorization(BlocwerkPolicies.WallGalleryImage)
            .DenyApiKeyPrincipals();
    }

    internal static async Task<IResult> HandleAsync(
        Guid wallId,
        string sha,
        [FromQuery] string? token,
        ClaimsPrincipal user,
        HttpContext http,
        [FromServices] IAssetResolver resolver,
        CancellationToken ct)
    {
        if (user.IsApiKeyPrincipal())
        {
            return Results.NotFound();
        }

        var asset = await resolver.ResolveAsync(wallId, sha, token, ct);
        if (asset is null)
        {
            return Results.NotFound();
        }

        return asset.IsStreamed
            ? await AssetResponse.StreamAsync(http, resolver, asset, ct)
            : await AssetResponse.OriginalAsync(http, resolver, asset, ct);
    }

    internal static async Task<IResult> HandleVariantAsync(
        Guid wallId,
        string sha,
        int width,
        [FromQuery] string? token,
        ClaimsPrincipal user,
        HttpContext http,
        [FromServices] IAssetResolver resolver,
        [FromServices] IImageVariantCache variants,
        CancellationToken ct)
    {
        if (user.IsApiKeyPrincipal() || !ImageVariants.IsAllowed(width))
        {
            return Results.NotFound();
        }

        var asset = await resolver.ResolveAsync(wallId, sha, token, ct);
        if (asset is not { HasVariants: true })
        {
            return Results.NotFound();
        }

        return await AssetResponse.VariantAsync(http, resolver, variants, asset, width, ct);
    }
}
