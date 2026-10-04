// <copyright file="WallDuplicateHoldCropEndpoint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The review picture of a possible duplicate: the panel photo around one hold with both outlines drawn. Made when the page
/// asks; the wall-admin check (and the kiosk refusal) is the service's own.
/// </summary>
public static class WallDuplicateHoldCropEndpoint
{
    public const string Route = "/api/walls/{wallId:guid}/duplicate-holds/{holdId:guid}/{otherId:guid}/crop";

    public static string Url(Guid wallId, Guid holdId, Guid otherId) =>
        $"/api/walls/{wallId}/duplicate-holds/{holdId}/{otherId}/crop";

    public static void MapWallDuplicateHoldCrops(this WebApplication app) =>
        app.MapGet(Route, HandleAsync).RequireAuthorization(BlocwerkPolicies.HumanOrUserApiKey);

    internal static async Task<IResult> HandleAsync(
        Guid wallId, Guid holdId, Guid otherId, HttpContext http, IHoldDuplicateService duplicates, CancellationToken ct)
    {
        try
        {
            var bytes = await duplicates.CropAsync(wallId, holdId, otherId, ct);
            if (bytes is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "private, max-age=300";
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
