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

    public static string Url(Guid wallId, Guid findingId, CheckCropView view, bool plain = false) =>
        $"/api/walls/{wallId}/look-over/findings/{findingId}/{view.ToString().ToLowerInvariant()}{(plain ? "?plain=true" : string.Empty)}";

    public static void MapWallLookOverCrops(this WebApplication app) =>
        app.MapGet(Route, HandleAsync).RequireAuthorization(BlocwerkPolicies.HumanOrUserApiKey);

    internal static async Task<IResult> HandleAsync(
        Guid wallId, Guid findingId, string view, HttpContext http, IWallLookOverService lookOver, CancellationToken ct, bool plain = false)
    {
        // A name only: Enum.TryParse would also take "7" and hand an undefined value to the service.
        if (!Enum.GetNames<CheckCropView>().Contains(view, StringComparer.OrdinalIgnoreCase) || !Enum.TryParse<CheckCropView>(view, ignoreCase: true, out var which))
        {
            return Results.NotFound();
        }

        try
        {
            var bytes = await lookOver.GetCropAsync(wallId, findingId, which, plain, ct);
            if (bytes is null)
            {
                // A finding of the wall without a 3D picture (no texture there, or an empty one) is not an error: 204,
                // which hides the tile without a failed request in the browser's console. Everything else is a 404.
                return which == CheckCropView.Model && await lookOver.HasFindingAsync(wallId, findingId, ct) ? Results.NoContent() : Results.NotFound();
            }

            // Revalidated on every use (only who may look over the wall sees them); an unchanged crop is a 304, and the
            // 3D picture that followed the hold's placement is the new bytes at once.
            return RevalidatedResponse.Bytes(http, bytes, "image/jpeg");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or KioskRestrictedException)
        {
            return Results.Forbid();
        }
    }
}
