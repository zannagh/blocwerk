using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The per-panel photo bytes for the browser on a big (multi-image) wall: the live promoted
/// photo and the staged (not-yet-confirmed) photo of a panel being added.
/// </summary>
/// <remarks>
/// Gated exactly like <see cref="WallGalleryImageEndpoint"/> — the pattern for wall media in this
/// codebase — rather than left open: a signed-in member of the wall, or an anonymous viewer holding
/// the wall's share token, and an API-key principal is rejected outright. The routes previously
/// carried no authorization at all and the app has no fallback policy, so two guessed GUIDs read a
/// panel photo of any wall from the open internet.
/// </remarks>
public static class WallPanelPhotoEndpoints
{
    public static void MapWallPanelPhotos(this WebApplication app)
    {
        app.MapMethods("/api/walls/{wallId:guid}/panels/{panelId:guid}/photo", Verbs, (
            Guid wallId,
            Guid panelId,
            [FromQuery] string? token,
            [FromQuery(Name = "w")] int? w,
            ClaimsPrincipal user,
            HttpContext http,
            [FromServices] IWallPanelService panelService,
            [FromServices] ICurrentUserService currentUserService,
            [FromServices] IDbContextFactory<BlocwerkDbContext> dbContextFactory,
            [FromServices] IKioskContext kioskContext,
            [FromServices] IImageVariantCache variants,
            CancellationToken ct) =>
                ServeAsync(
                    wallId, panelId, token, w, user, http, currentUserService, dbContextFactory, kioskContext,
                    variants, "live",
                    () => panelService.GetPanelPhotoTagAsync(wallId, panelId),
                    () => panelService.GetPanelPhotoAsync(wallId, panelId), ct))
            .RequireAuthorization(BlocwerkPolicies.WallGalleryImage)
            .DenyApiKeyPrincipals();

        app.MapMethods("/api/walls/{wallId:guid}/panels/{panelId:guid}/staged-photo", Verbs, (
            Guid wallId,
            Guid panelId,
            [FromQuery] string? token,
            [FromQuery(Name = "w")] int? w,
            ClaimsPrincipal user,
            HttpContext http,
            [FromServices] IWallPanelService panelService,
            [FromServices] ICurrentUserService currentUserService,
            [FromServices] IDbContextFactory<BlocwerkDbContext> dbContextFactory,
            [FromServices] IKioskContext kioskContext,
            [FromServices] IImageVariantCache variants,
            CancellationToken ct) =>
                ServeAsync(
                    wallId, panelId, token, w, user, http, currentUserService, dbContextFactory, kioskContext,
                    variants, "staged",
                    () => panelService.GetPanelStagedPhotoTagAsync(wallId, panelId),
                    () => panelService.GetPanelStagedPhotoAsync(wallId, panelId), ct))
            .RequireAuthorization(BlocwerkPolicies.WallGalleryImage)
            .DenyApiKeyPrincipals();
    }

    private static readonly string[] Verbs = [HttpMethods.Get, HttpMethods.Head];

    private static async Task<IResult> ServeAsync(
        Guid wallId,
        Guid panelId,
        string? token,
        int? width,
        ClaimsPrincipal user,
        HttpContext http,
        ICurrentUserService currentUserService,
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IKioskContext kioskContext,
        IImageVariantCache variants,
        string slot,
        Func<Task<WallPhotoTag?>> loadTag,
        Func<Task<WallPhoto?>> load,
        CancellationToken ct)
    {
        if (user.IsApiKeyPrincipal() || !ImageResponse.IsRenderableWidth(width))
        {
            return Results.NotFound();
        }

        if (!await HasWallAccessAsync(wallId, token, currentUserService, dbContextFactory, kioskContext, ct))
        {
            return Results.NotFound();
        }

        // The panel/wall pairing itself is already enforced by the service, which matches on both
        // ids — so a panel of another wall cannot be pulled through an authorized wallId. That holds
        // for the metadata read as well, which is the same row under the same pairing.
        var tag = await loadTag();
        if (tag is null)
        {
            return Results.NotFound();
        }

        // A big wall is drawn entirely out of these routes — one multi-megabyte panel per grid cell —
        // so the 304 here is what keeps a tablet from redownloading the whole wall on every
        // navigation. The bytes are only loaded inside the callback, on a miss.
        return await ImageResponse.ServeAsync(
            http,
            variants,
            width,
            tag,
            immutable: false,
            async () => (await load())?.Photo,
            panelId, slot);
    }

    /// <summary>
    /// The same two gates as the gallery bytes — a matching share token, or membership of the wall —
    /// but accepted as EITHER rather than one or the other. Each is independently sufficient, and a
    /// panel image request carries whatever token the page was opened with: enhanced-nav keeps route
    /// params and the last-page cookie can restore a /shared/ link, so a member browsing under a
    /// stale token would otherwise get a wall full of broken images.
    /// </summary>
    private static async Task<bool> HasWallAccessAsync(
        Guid wallId,
        string? token,
        ICurrentUserService currentUserService,
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IKioskContext kioskContext,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(token))
        {
            await using var anonymousDb = await dbContextFactory.CreateDbContextAsync(ct);
            anonymousDb.CurrentUserId = Guid.Empty;
            if (await anonymousDb.Walls.AnyAsync(w => w.Id == wallId && w.ShareToken == token, ct))
            {
                return true;
            }
        }

        User user;
        try
        {
            user = await currentUserService.GetCurrentUserAsync();
        }
        catch (UnauthorizedAccessException)
        {
            // Nobody is signed in. With a token that just means it did not match; without one the
            // caller is anonymous on a non-shared wall — EXCEPT for a registered kiosk tablet asking
            // for the wall it is bolted to, which is the state it sits in for most of the day. A big
            // (multi-image) wall is drawn entirely out of these panel bytes, so refusing them leaves
            // the tablet showing an empty frame. Any other wall is still refused, and a 404 says
            // less than a challenge would.
            return KioskViewing.AllowsAnonymousViewOf(kioskContext, wallId);
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;

        return await db.Walls.AnyAsync(w => w.Id == wallId, ct);
    }
}
