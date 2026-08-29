using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The per-panel photo bytes for the browser on a big (multi-image) wall: the live promoted
/// photo and the staged (not-yet-confirmed) photo of a panel being added.
/// </summary>
/// <remarks>
/// Mirrors <see cref="WallPhotoEndpoints"/>: these routes sit under <c>/api/walls</c> but are
/// browser routes gated on the signed-in caller, so an API-key principal is rejected outright.
/// </remarks>
public static class WallPanelPhotoEndpoints
{
    public static void MapWallPanelPhotos(this WebApplication app)
    {
        app.MapGet("/api/walls/{wallId:guid}/panels/{panelId:guid}/photo", async (
            Guid wallId,
            Guid panelId,
            ClaimsPrincipal user,
            [FromServices] IWallPanelService panelService) =>
        {
            if (user.IsApiKeyPrincipal())
            {
                return Results.NotFound();
            }

            var photo = await panelService.GetPanelPhotoAsync(wallId, panelId);
            return photo == null
                ? Results.NotFound()
                : Results.File(photo.Photo, photo.ContentType ?? "image/jpeg");
        }).DenyApiKeyPrincipals();

        app.MapGet("/api/walls/{wallId:guid}/panels/{panelId:guid}/staged-photo", async (
            Guid wallId,
            Guid panelId,
            ClaimsPrincipal user,
            [FromServices] IWallPanelService panelService) =>
        {
            if (user.IsApiKeyPrincipal())
            {
                return Results.NotFound();
            }

            var photo = await panelService.GetPanelStagedPhotoAsync(wallId, panelId);
            return photo == null
                ? Results.NotFound()
                : Results.File(photo.Photo, photo.ContentType ?? "image/jpeg");
        }).DenyApiKeyPrincipals();
    }
}
