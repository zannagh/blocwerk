// <copyright file="WallRefreshReviewCropEndpoint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Refresh.Review;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The pictures of a review item (<c>old</c>, <c>new</c> or <c>model</c>), by the item's key. Made when the page asks and
/// cached by the service; the wall-admin check is the service's own.
/// </summary>
public static class WallRefreshReviewCropEndpoint
{
    public const string Route = "/api/refreshes/{refreshId:guid}/review/{key}/{view}";

    public static string Url(Guid refreshId, string key, CheckCropView view) =>
        $"/api/refreshes/{refreshId}/review/{key}/{view.ToString().ToLowerInvariant()}";

    public static void MapWallRefreshReviewCrops(this WebApplication app) =>
        app.MapGet(Route, HandleAsync).RequireAuthorization(BlocwerkPolicies.HumanOrUserApiKey);

    internal static async Task<IResult> HandleAsync(
        Guid refreshId, string key, string view, HttpContext http, IUpdateReviewService review, CancellationToken ct)
    {
        if (!Enum.TryParse<CheckCropView>(view, ignoreCase: true, out var which))
        {
            return Results.NotFound();
        }

        try
        {
            var bytes = await review.GetCropAsync(refreshId, key, which, ct);
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
