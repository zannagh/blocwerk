// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Visits;

/// <inheritdoc cref="IWallVisitService"/>
/// <remarks>
/// A visit grants viewing and nothing else. It does not make the visitor a user: they stay anonymous, so every write
/// in the app, which needs a signed-in user, refuses them on its own. What the grant adds is read access to the one
/// wall, through <see cref="KioskViewing"/> and the wall stamp of <see cref="KioskScopedDbContextFactory"/>, the same
/// two places that let a resting kiosk tablet read its wall.
/// </remarks>
public sealed class WallVisitService(
    IDbContextFactory<BlocwerkDbContext> contexts,
    ICurrentUserService currentUser,
    ILogger<WallVisitService> logger,
    IKioskContext? kioskContext = null,
    TimeProvider? clock = null) : IWallVisitService
{
    /// <summary>How many links one member may make within an hour.</summary>
    public const int HourlyLimit = 20;

    /// <summary>How long a link, and a visit made with it, lasts.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>How long a row is kept after it expired, then deleted when the next link is made.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private TimeProvider Clock => clock ?? TimeProvider.System;

    /// <inheritdoc/>
    public async Task<VisitLinkCreated> CreateAsync(Guid wallId, CancellationToken ct = default)
    {
        // Throws for anybody anonymous, which is every visitor: a visit never makes a user.
        var user = await currentUser.GetCurrentUserAsync();
        await using var db = await contexts.CreateDbContextAsync(ct);

        // A tablet in the gym hands out nothing that outlives its session, the same rule as the invite link.
        KioskGuard.EnsureNotKiosk(kioskContext, db, "Making a visitor link");
        await EnsureMayInviteAsync(db, wallId, user, ct);

        var now = Clock.GetUtcNow();
        var since = now - TimeSpan.FromHours(1);
        var recent = await db.WallVisitLinks.CountAsync(l => l.CreatedByUserId == user.Id && l.CreatedAt > since, ct);
        if (recent >= HourlyLimit)
        {
            throw new VisitLinkLimitException();
        }

        var stale = now - Retention;
        await db.WallVisitLinks.Where(l => l.ExpiresAt < stale).ExecuteDeleteAsync(ct);

        var token = VisitLinkTokens.Create();
        db.WallVisitLinks.Add(new WallVisitLink
        {
            WallId = wallId,
            CreatedByUserId = user.Id,
            TokenHash = VisitLinkTokens.Hash(token),
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Visitor link made for wall {WallId} by {UserId}", wallId, user.Id);
        return new VisitLinkCreated(token, now + Lifetime);
    }

    /// <inheritdoc/>
    public async Task<VisitLinkLookup> LookUpAsync(string token, CancellationToken ct = default)
    {
        if (!VisitLinkTokens.IsWellFormed(token))
        {
            return VisitLinkLookup.NotFound;
        }

        var hash = VisitLinkTokens.Hash(token);
        await using var db = await contexts.CreateDbContextAsync(ct);

        // The token is the authority here, not the session, so the wall is read past the membership and kiosk filters.
        // A link whose maker has left the wall reads as no link at all, as it is once the leaving deleted it.
        var row = await db.WallVisitLinks.AsNoTracking()
            .Where(l => l.TokenHash == hash)
            .Where(VisitLinkCreators.StillMayInvite(db))
            .Select(l => new
            {
                l.Id,
                l.WallId,
                l.ExpiresAt,
                l.RedemptionId,
                WallName = db.Walls.IgnoreQueryFilters().Where(w => w.Id == l.WallId).Select(w => w.Name).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return VisitLinkLookup.NotFound;
        }

        var status = Clock.GetUtcNow() >= row.ExpiresAt ? VisitLinkStatus.Expired
            : row.RedemptionId is null ? VisitLinkStatus.Open
            : VisitLinkStatus.Redeemed;
        return new VisitLinkLookup(status, row.Id, row.WallId, row.WallName ?? string.Empty, row.ExpiresAt, row.RedemptionId);
    }

    /// <inheritdoc/>
    public async Task<VisitGrant?> RedeemAsync(string token, Guid claimId, CancellationToken ct = default)
    {
        if (!VisitLinkTokens.IsWellFormed(token) || claimId == Guid.Empty)
        {
            return null;
        }

        var hash = VisitLinkTokens.Hash(token);
        var now = Clock.GetUtcNow();

        // Not cancelled with the request: once the update below has committed, the grant must be read and handed out,
        // or the link is used and its guest has no cookie.
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);

        // The one write that decides who got the link: the WHERE carries "unused and unexpired" and "its maker may still
        // invite", so the database lets exactly one of two concurrent attempts change the row, and the other changes nothing.
        await db.WallVisitLinks
            .Where(l => l.TokenHash == hash && l.RedemptionId == null && l.ExpiresAt > now)
            .Where(VisitLinkCreators.StillMayInvite(db))
            .ExecuteUpdateAsync(
                s => s.SetProperty(l => l.RedemptionId, (Guid?)claimId).SetProperty(l => l.RedeemedAt, (DateTimeOffset?)now),
                CancellationToken.None);

        // Whether this attempt won or an earlier one with the same claim did (a double tap of the same page, whose
        // first answer the browser dropped): either way the link's redemption is this claim, and only this page has it.
        var link = await db.WallVisitLinks.AsNoTracking()
            .Where(l => l.TokenHash == hash && l.RedemptionId == claimId && l.ExpiresAt > now)
            .Where(VisitLinkCreators.StillMayInvite(db))
            .Select(l => new { l.Id, l.WallId, l.ExpiresAt })
            .FirstOrDefaultAsync(CancellationToken.None);
        if (link is null)
        {
            return null;
        }

        logger.LogInformation("Visitor link {LinkId} used for wall {WallId}", link.Id, link.WallId);
        return new VisitGrant(link.Id, link.WallId, claimId, link.ExpiresAt);
    }

    /// <inheritdoc/>
    public async Task<bool> CallerIsMemberAsync(Guid wallId, CancellationToken ct = default)
    {
        User user;
        try
        {
            user = await currentUser.GetCurrentUserAsync();
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Walls.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(w => w.Id == wallId && (w.OwnerId == user.Id || w.Members.Any(m => m.UserId == user.Id)), ct);
    }

    /// <summary>
    /// Members of every role and the owner may invite a visitor, as they may hand out the invite link; so may a site
    /// admin. Read past the query filters, because a legacy owner has no member row, and checked explicitly here.
    /// </summary>
    private static async Task EnsureMayInviteAsync(BlocwerkDbContext db, Guid wallId, User user, CancellationToken ct)
    {
        var wall = await db.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => new { w.OwnerId, IsMember = w.Members.Any(m => m.UserId == user.Id) })
            .FirstOrDefaultAsync(ct);
        if (wall is null)
        {
            throw new UnauthorizedAccessException($"Wall {wallId} does not exist or is not yours to share.");
        }

        if (!wall.IsMember && wall.OwnerId != user.Id && user.Role != IdentityRole.Admin)
        {
            throw new UnauthorizedAccessException($"User {user.Id} may not invite visitors to wall {wallId}.");
        }
    }
}
