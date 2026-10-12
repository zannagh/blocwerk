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
/// asks; who may look over the wall is the service's own check, and a finding of another wall is not found.
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
        // A name only: Enum.TryParse would also take "7" and hand an undefined value to the service.
        if (!Enum.GetNames<CheckCropView>().Contains(view, StringComparer.OrdinalIgnoreCase) || !Enum.TryParse<CheckCropView>(view, ignoreCase: true, out var which))
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

            // Never in a shared cache (only who may look over the wall sees them). The two photo crops never change;
            // the 3D picture follows the hold's placement, so the browser asks again each time.
            http.Response.Headers.CacheControl = which == CheckCropView.Model ? "private, no-store" : "private, max-age=3600";
            return Results.File(bytes, "image/jpeg");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or KioskRestrictedException)
        {
            return Results.Forbid();
        }
    }
}
