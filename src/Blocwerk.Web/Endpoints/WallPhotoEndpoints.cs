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
/// The wall photo bytes for the browser: the current photo and the photo as it looked at a given
/// generation (so historic boulders render against the wall they were actually set on).
/// </summary>
/// <remarks>
/// These routes live under <c>/api/walls</c>, which is a prefix on which an API key is allowed to
/// authenticate, but they are NOT machine routes: they gate on what the signed-in caller may see
/// (or on a share token), never on the wall the key was issued for. An API-key principal is
/// therefore rejected outright — otherwise a key for wall A would read the photos of every wall
/// its owner belongs to. Machine callers use <c>/api/walls/{wallId}/images/…</c>, which compares
/// the route's wall against the key's own wall claim.
/// <para>
/// Each route resolves a <see cref="WallPhotoTag"/> first — metadata only, under the same gate as
/// the byte call it precedes — and hands the blob load to <see cref="ImageResponse"/> as a
/// callback, so a revalidation that ends in 304 never reads the photo out of Postgres.
/// </para>
/// <para>
/// The current-generation <c>/photo</c> is served from the wall's CENTER <see cref="WallPanel"/>
/// (grid slot 0,0) rather than the retiring <c>Wall.Photo</c>: every wall is now a big wall whose
/// canonical image is that panel. It reuses <see cref="WallPanelPhotoEndpoints"/>' byte-serving
/// (tag/ETag, variant cache, panel identity) so the two routes cache identically. When NO live
/// centre panel resolves (a converge gap that left the wall without a (0,0) panel) it falls back to
/// the retiring <c>Wall.Photo</c>, served under the same gate, so the hero and boulder-fallback
/// images do not 404 wall-wide. The historic <c>/photo/{generation}</c> still reads the archived
/// photo off the reset row — that record is untouched by the panel model.
/// </para>
/// </remarks>
public static class WallPhotoEndpoints
{
    public static void MapWallPhotos(this WebApplication app)
    {
        app.MapMethods("/api/walls/{wallId:guid}/photo", [HttpMethods.Get, HttpMethods.Head], async (
            Guid wallId,
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
        {
            if (user.IsApiKeyPrincipal() || !ImageResponse.IsRenderableWidth(w))
            {
                return Results.NotFound();
            }

            // Authorize AND resolve in one step: the returned id exists only when the caller may see
            // the wall (share token, membership, or an anonymous kiosk on its own wall) and a live
            // centre panel is present.
            var panelId = await ResolveCenterPanelIdAsync(
                wallId, token, currentUserService, dbContextFactory, kioskContext, ct);
            if (panelId is null)
            {
                // SAFETY NET: no live centre panel resolved. Usually that means the caller may not
                // see the wall, but it also covers a startup convergence gap — a wall left with
                // Wall.Photo bytes but no (0,0) panel because its per-wall converge threw and was
                // skipped. Rather than 404 the hero image AND every "/photo" boulder fallback
                // wall-wide, fall back to the retiring Wall.Photo. It is served under its OWN
                // token-or-membership-or-kiosk gate (GetPhotoTagAsync/GetPhotoAsync), exactly as the
                // pre-panel /photo did on main, so a denied caller or a genuinely photo-less wall
                // still 404s and the anonymous+token / anonymous-kiosk cases keep working.
                return await ServeWallPhotoFallbackAsync(wallId, token, w, http, wallService, variants);
            }

            // The panel tag/bytes calls do not re-gate (WallPanels carries no query filter), which is
            // why the access check above is load-bearing; the wall/panel pairing is still enforced by
            // the service. Identity, tag and immutability match WallPanelPhotoEndpoints exactly so a
            // panel already cached under /panels/{id}/photo revalidates here without a re-download.
            var tag = await panelService.GetPanelPhotoTagAsync(wallId, panelId.Value);
            if (tag is null)
            {
                return Results.NotFound();
            }

            return await ImageResponse.ServeAsync(
                http,
                variants,
                w,
                tag,
                immutable: false,
                async () => (await panelService.GetPanelPhotoAsync(wallId, panelId.Value))?.Photo,
                ImageIdentity.PanelPhoto(panelId.Value, ImageIdentity.LiveSlot));
        }).DenyApiKeyPrincipals();

        app.MapMethods("/api/walls/{wallId:guid}/photo/{generation:int}", [HttpMethods.Get, HttpMethods.Head], async (
            Guid wallId,
            int generation,
            [FromQuery] string? token,
            [FromQuery(Name = "w")] int? w,
            ClaimsPrincipal user,
            HttpContext http,
            [FromServices] IWallService wallService,
            [FromServices] IImageVariantCache variants) =>
        {
            if (user.IsApiKeyPrincipal() || !ImageResponse.IsRenderableWidth(w))
            {
                return Results.NotFound();
            }

            var tag = await wallService.GetPhotoTagForGenerationAsync(wallId, token, generation);
            if (tag is null)
            {
                return Results.NotFound();
            }

            // A retired generation's photo is archived on its reset row and never rewritten, so this
            // route is content-addressed and the browser is told it need not ask again. A generation
            // at or above the current one resolves to the LIVE photo, which is mutable — hence the
            // flag off the tag rather than off the route shape.
            return await ImageResponse.ServeAsync(
                http,
                variants,
                w,
                tag,
                tag.IsArchived,
                async () =>
                {
                    var photo = string.IsNullOrEmpty(token)
                        ? await wallService.GetPhotoForGenerationAsync(wallId, generation)
                        : await wallService.GetPhotoForGenerationByShareTokenAsync(wallId, token, generation);
                    return photo?.Photo;
                },
                ImageIdentity.WallGenerationPhoto(wallId, generation));
        }).DenyApiKeyPrincipals();
    }

    /// <summary>
    /// The id of the wall's live centre panel (grid slot 0,0), or null when the caller may not see
    /// the wall OR no live centre panel exists yet. Mirrors the EITHER/OR gate of
    /// <c>WallPanelPhotoEndpoints.HasWallAccessAsync</c>: a matching share token is sufficient, but a
    /// token MISMATCH does not shadow a signed-in member — it falls through to the membership filter
    /// (with the anonymous-kiosk allowance for a tablet on its own wall) rather than 404ing. That way
    /// a member browsing with a stale/foreign <c>?token=</c> resolves the same centre panel that
    /// <c>/panels/{id}/photo</c> already serves them. Both "not allowed" and "no panel" collapse to
    /// null; the caller then falls back to Wall.Photo (itself gated) before finally 404ing.
    /// </summary>
    private static async Task<Guid?> ResolveCenterPanelIdAsync(
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
                return await CenterPanelIdAsync(anonymousDb, wallId, ct);
            }

            // Token did not match. Do NOT return here: fall through to the membership gate below so a
            // signed-in member carrying a stale token in the URL is still served.
        }

        Guid viewerId;
        try
        {
            var currentUser = await currentUserService.GetCurrentUserAsync();
            viewerId = currentUser.Id;
        }
        catch (UnauthorizedAccessException)
        {
            // Nobody is signed in and no token was presented. The only anonymous caller allowed the
            // current photo is a registered kiosk asking for the wall it is bolted to; any other
            // wall stays refused. Guid.Empty then reads under the Wall filter's see-all branch,
            // already narrowed to that single wall by the kiosk stamp on the context.
            if (!KioskViewing.AllowsAnonymousViewOf(kioskContext, wallId))
            {
                return null;
            }

            viewerId = Guid.Empty;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = viewerId;

        // WallPanels carries no query filter of its own, so the wall lookup — which DOES honour the
        // membership filter — is what gates a signed-in caller: a wall they are not a member of is
        // invisible here and yields no centre panel.
        if (!await db.Walls.AnyAsync(w => w.Id == wallId, ct))
        {
            return null;
        }

        return await CenterPanelIdAsync(db, wallId, ct);
    }

    /// <summary>
    /// Serves the retiring <see cref="Wall.Photo"/> bytes as the current-generation photo, the way the
    /// pre-panel <c>/photo</c> did on <c>main</c>: the wall service's tag/byte calls apply the same
    /// token-or-membership-or-kiosk gate, so a caller who may not see the wall (or a wall with no
    /// stored photo) still 404s. Reused only as the safety net when no live centre panel resolves.
    /// ETag/caching is preserved: the tag drives revalidation and <see cref="ImageIdentity.WallPhoto"/>
    /// keys the variant cache exactly as it did before the panel model.
    /// </summary>
    private static async Task<IResult> ServeWallPhotoFallbackAsync(
        Guid wallId,
        string? token,
        int? w,
        HttpContext http,
        IWallService wallService,
        IImageVariantCache variants)
    {
        var tag = await wallService.GetPhotoTagAsync(wallId, token);
        if (tag is null)
        {
            return Results.NotFound();
        }

        return await ImageResponse.ServeAsync(
            http,
            variants,
            w,
            tag,
            immutable: false,
            () => string.IsNullOrEmpty(token)
                ? wallService.GetPhotoAsync(wallId)
                : wallService.GetPhotoByShareTokenAsync(wallId, token),
            ImageIdentity.WallPhoto(wallId));
    }

    /// <summary>
    /// The live centre panel for the wall — slot (0,0) with a promoted photo — preferring the
    /// latest generation, mirroring the per-cell dedupe <see cref="IWallPanelService.GetPanelsAsync"/>
    /// applies so a superseded row never wins. Null when the wall has no such panel.
    /// </summary>
    private static async Task<Guid?> CenterPanelIdAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var id = await db.WallPanels
            .AsNoTracking()
            .Where(p => p.WallId == wallId && p.Col == 0 && p.Row == 0 && p.Photo != null)
            .OrderByDescending(p => p.Generation)
            .Select(p => p.Id)
            .FirstOrDefaultAsync(ct);

        return id == Guid.Empty ? null : id;
    }
}
