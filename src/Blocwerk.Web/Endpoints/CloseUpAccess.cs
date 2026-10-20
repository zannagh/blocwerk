// <copyright file="CloseUpAccess.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services.HybridZoom;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Who may see the hybrid zoom's close-ups. They are cut from the 3D capture's photos, which are otherwise wall-admin
/// only (<see cref="CapturePhotoEndpoint"/>) and can show what the panel photos were framed to leave out (people,
/// the room), so by default only a signed-in member of the wall gets them — no share-token viewer, no kiosk tablet,
/// nobody anonymous. <see cref="HybridZoomOptions.AllowShareAndKiosk"/> widens that to the gallery rule
/// (<see cref="WallPanelPhotoEndpoints"/>): a matching share token, a member, or the wall's own kiosk. Either way a
/// token that does not match falls through to membership, so a member browsing under a stale share link still gets in.
/// </summary>
internal static class CloseUpAccess
{
    /// <summary>Whether the caller may see close-ups of the wall.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="token">The share token the page carries, if any.</param>
    /// <param name="options">The hybrid zoom options.</param>
    /// <param name="currentUserService">The signed-in user.</param>
    /// <param name="dbContextFactory">The contexts.</param>
    /// <param name="kioskContext">The kiosk state of the request.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when allowed.</returns>
    public static async Task<bool> AllowsAsync(
        Guid wallId,
        string? token,
        HybridZoomOptions options,
        ICurrentUserService currentUserService,
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IKioskContext kioskContext,
        CancellationToken ct)
    {
        var wide = options.AllowShareAndKiosk;
        if (wide && !string.IsNullOrEmpty(token) && await TokenMatchesAsync(wallId, token, dbContextFactory, ct))
        {
            return true;
        }

        if (!wide && kioskContext.IsKiosk)
        {
            return false;
        }

        User user;
        try
        {
            user = await currentUserService.GetCurrentUserAsync();
        }
        catch (UnauthorizedAccessException)
        {
            return wide && KioskViewing.AllowsAnonymousViewOf(kioskContext, wallId);
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;
        return await db.Walls.AnyAsync(w => w.Id == wallId, ct);
    }

    private static async Task<bool> TokenMatchesAsync(Guid wallId, string token, IDbContextFactory<BlocwerkDbContext> dbContextFactory, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        return await db.Walls.AnyAsync(w => w.Id == wallId && w.ShareToken == token, ct);
    }
}
