// <copyright file="WallEnhancementCropEndpoint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The review picture of one wall-enhancement item (<see cref="IWallEnhancementService.CropAsync"/>): the hold on its
/// panel photo, with the old shape or the pair's other hold dashed. Wall admins only, like the review itself.
/// </summary>
public static class WallEnhancementCropEndpoint
{
    public const string Route = "/api/walls/{wallId:guid}/enhancement/items/{itemId:guid}/crop";

    public static string Url(Guid wallId, Guid itemId) => $"/api/walls/{wallId}/enhancement/items/{itemId}/crop";

    public static void MapWallEnhancementCrops(this WebApplication app) =>
        app.MapGet(Route, HandleAsync).RequireAuthorization(BlocwerkPolicies.HumanOrUserApiKey);

    internal static async Task<IResult> HandleAsync(
        Guid wallId, Guid itemId, HttpContext http, IWallEnhancementService enhancement, CancellationToken ct)
    {
        try
        {
            var bytes = await enhancement.CropAsync(wallId, itemId, ct);
            if (bytes is null)
            {
                return Results.NotFound();
            }

            // A decision can change what the picture shows (an undone shape): short private cache only.
            http.Response.Headers.CacheControl = "private, max-age=30";
            return Results.File(bytes, "image/jpeg");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or KioskRestrictedException)
        {
            return Results.Forbid();
        }
        catch (UserFacingException)
        {
            return Results.NotFound();
        }
    }
}
