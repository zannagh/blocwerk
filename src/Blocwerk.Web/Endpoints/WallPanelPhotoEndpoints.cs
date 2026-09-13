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
            [FromServices] IWallService wallService,
            [FromServices] ICurrentUserService currentUserService,
            [FromServices] IDbContextFactory<BlocwerkDbContext> dbContextFactory,
            [FromServices] IKioskContext kioskContext,
            [FromServices] IImageVariantCache variants,
            CancellationToken ct) =>
                ServeAsync(
                    wallId, panelId, token, w, user, http, currentUserService, dbContextFactory, kioskContext,
                    variants, ImageIdentity.LiveSlot,
                    () => panelService.GetPanelPhotoTagAsync(wallId, panelId),
                    () => panelService.GetPanelPhotoAsync(wallId, panelId),
                    // A panel with no committed blob of its own — a staged next-generation row, or a
                    // single-image-migrated wall's origin — falls back to the image it is replacing: the
                    // latest committed panel at the SAME (Col,Row) from an earlier generation, and only for
                    // the (0,0) origin with none of those, the legacy Wall.Photo. So the overlap stepper's
                    // "existing neighbour" resolves instead of rendering a 404 box.
                    () => ServeReplacedPanelPhotoFallbackAsync(
                        wallId, panelId, token, w, http, panelService, wallService, dbContextFactory, variants, ct),
                    ct))
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
                    variants, ImageIdentity.StagedSlot,
                    () => panelService.GetPanelStagedPhotoTagAsync(wallId, panelId),
                    () => panelService.GetPanelStagedPhotoAsync(wallId, panelId),
                    // A staged photo that is genuinely absent must 404 — no Wall.Photo fallback here.
                    notFoundFallback: null, ct))
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
        Func<Task<IResult>>? notFoundFallback,
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
            // No committed blob on this panel. The live route hands us a fallback that serves the image
            // this panel is replacing — the latest committed panel at the same (Col,Row), or Wall.Photo
            // for the (0,0) origin — and 404s otherwise; the staged route passes none, so an absent staged
            // photo still 404s.
            return notFoundFallback is null ? Results.NotFound() : await notFoundFallback();
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
            ImageIdentity.PanelPhoto(panelId, slot));
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

    /// <summary>
    /// Fallback for a panel with NO committed photo blob of its own — a staged next-generation row that
    /// has not been promoted, or a single-image-migrated wall's origin panel. Serves, in order:
    /// <list type="number">
    /// <item>the latest COMMITTED panel at the SAME <c>(Col,Row)</c> from an earlier generation — the
    /// image this panel is replacing (immutable-generation model keeps the superseded row's photo);</item>
    /// <item>for the <c>(0,0)</c> origin with no such prior panel, the legacy <see cref="Wall.Photo"/>.</item>
    /// </list>
    /// Any other position with neither still 404s. The replacement is always chosen by an exact
    /// <c>(Col,Row)</c> match, so one grid position's photo is never served for a different one. Caller
    /// has already passed the wall-access gate; the prior panel's bytes go through the same
    /// <see cref="IWallPanelService"/> read the primary path uses and the Wall.Photo tier is gated AGAIN
    /// through the wall service (token/membership). Each tier keys under the identity of the row whose
    /// bytes it serves, so variants dedupe with that row's own <c>/photo</c> and the wall-level fallback.
    /// </summary>
    private static async Task<IResult> ServeReplacedPanelPhotoFallbackAsync(
        Guid wallId,
        Guid panelId,
        string? token,
        int? width,
        HttpContext http,
        IWallPanelService panelService,
        IWallService wallService,
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IImageVariantCache variants,
        CancellationToken ct)
    {
        // WallPanels carries no query filter; the wall-access gate ran already. Locate the requested
        // panel's grid position (scoped to this wall), then find the image it is replacing.
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var position = await db.WallPanels
            .AsNoTracking()
            .Where(p => p.Id == panelId && p.WallId == wallId)
            .Select(p => new { p.Col, p.Row })
            .FirstOrDefaultAsync(ct);
        if (position is null)
        {
            return Results.NotFound();
        }

        // The latest COMMITTED panel at the exact same (Col,Row) — never a different position, and never
        // the requested row itself. This is the previous generation's live image being replaced.
        var replacedPanelId = await db.WallPanels
            .AsNoTracking()
            .Where(p => p.WallId == wallId && p.Col == position.Col && p.Row == position.Row
                && p.Id != panelId && p.Photo != null)
            .OrderByDescending(p => p.Generation)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct);
        if (replacedPanelId is { } priorId)
        {
            var priorTag = await panelService.GetPanelPhotoTagAsync(wallId, priorId);
            if (priorTag is not null)
            {
                return await ImageResponse.ServeAsync(
                    http,
                    variants,
                    width,
                    priorTag,
                    immutable: false,
                    async () => (await panelService.GetPanelPhotoAsync(wallId, priorId))?.Photo,
                    ImageIdentity.PanelPhoto(priorId, ImageIdentity.LiveSlot));
            }
        }

        // No prior committed panel at this position. Only the (0,0) origin of a single-image-migrated
        // wall falls back to the legacy Wall.Photo; any other position 404s.
        if (position.Col != 0 || position.Row != 0)
        {
            return Results.NotFound();
        }

        var tag = await wallService.GetPhotoTagAsync(wallId, token);
        if (tag is null)
        {
            return Results.NotFound();
        }

        return await ImageResponse.ServeAsync(
            http,
            variants,
            width,
            tag,
            immutable: false,
            () => string.IsNullOrEmpty(token)
                ? wallService.GetPhotoAsync(wallId)
                : wallService.GetPhotoByShareTokenAsync(wallId, token),
            ImageIdentity.WallPhoto(wallId));
    }
}
