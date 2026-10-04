// <copyright file="WallRefreshCheckCropEndpoint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The pictures of a confirm-screen card: <c>old</c>, <c>new</c> or <c>model</c> (this visit's 3D texture). Made when
/// the page asks and cached by the service; the wall-admin check is the service's own.
/// </summary>
public static class WallRefreshCheckCropEndpoint
{
    public const string Route = "/api/refreshes/{refreshId:guid}/checks/{checkId:guid}/{view}";

    public static string Url(Guid refreshId, Guid checkId, CheckCropView view) =>
        $"/api/refreshes/{refreshId}/checks/{checkId}/{view.ToString().ToLowerInvariant()}";

    public static void MapWallRefreshCheckCrops(this WebApplication app) =>
        app.MapGet(Route, HandleAsync).RequireAuthorization(BlocwerkPolicies.HumanOrUserApiKey);

    internal static async Task<IResult> HandleAsync(
        Guid refreshId, Guid checkId, string view, HttpContext http, IWallRefreshService refreshes, CancellationToken ct)
    {
        if (!Enum.TryParse<CheckCropView>(view, ignoreCase: true, out var which))
        {
            return Results.NotFound();
        }

        try
        {
            var bytes = await refreshes.GetCheckCropAsync(refreshId, checkId, which, ct);
            if (bytes is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "private, max-age=3600";
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
