// <copyright file="WallLookOverCropEndpoint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The pictures of a look-over finding: <c>old</c>, <c>new</c> or <c>model</c> (the 3D texture). Made when the page
/// asks and cached by the service; who may look over the wall is the service's own check.
/// </summary>
public static class WallLookOverCropEndpoint
{
    public const string Route = "/api/walls/{wallId:guid}/look-over/findings/{findingId:guid}/{view}";

    public static string Url(Guid wallId, Guid findingId, CheckCropView view) =>
        $"/api/walls/{wallId}/look-over/findings/{findingId}/{view.ToString().ToLowerInvariant()}";

    public static void MapWallLookOverCrops(this WebApplication app) =>
        app.MapGet(Route, HandleAsync).RequireAuthorization(BlocwerkPolicies.HumanOrUserApiKey);

    internal static async Task<IResult> HandleAsync(
        Guid wallId, Guid findingId, string view, HttpContext http, IWallLookOverService lookOver, CancellationToken ct)
    {
        if (!Enum.TryParse<CheckCropView>(view, ignoreCase: true, out var which))
        {
            return Results.NotFound();
        }

        try
        {
            var bytes = await lookOver.GetCropAsync(wallId, findingId, which, ct);
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
    }
}
