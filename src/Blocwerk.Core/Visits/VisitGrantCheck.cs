// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Visits;

/// <summary>
/// Whether a visit cookie's grant still stands: its link exists, was redeemed by this grant, and its maker may still
/// invite to the wall (<see cref="VisitLinkCreators"/>). The cookie alone outlived the link, so a member's removal did not
/// end a visit already under way.
/// </summary>
/// <remarks>
/// Read through <see cref="RootDbContextFactory"/>: the session's own factory is stamped from the kiosk context, which is
/// the one asking.
/// </remarks>
public sealed class VisitGrantCheck(RootDbContextFactory contexts)
{
    public async Task<bool> IsLiveAsync(VisitGrant grant, CancellationToken ct = default)
    {
        await using var db = contexts.CreateDbContext();
        return await db.WallVisitLinks.AsNoTracking()
            .Where(l => l.Id == grant.LinkId && l.WallId == grant.WallId && l.RedemptionId == grant.RedemptionId)
            .Where(VisitLinkCreators.StillMayInvite(db))
            .AnyAsync(ct)
            .ConfigureAwait(false);
    }
}
