// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The walls whose views show something of a user: their memberships, the walls they own, hold the maintenance lock of
/// or staged, and the walls of the boulders, comments, ticks, ratings, grade proposals, setter credits, clips, log
/// entries, hold links and resets they made. An account erasure or merge changes how all of these render (a name, an
/// owner, a member list), so each gets a <c>wall.resync</c> (<see cref="FeedAccountRows"/>). Read before the operation's
/// writes, since they move or delete the rows that answer it.
/// </summary>
internal static class FeedUserFootprint
{
    public static async Task<IReadOnlyList<Guid>> WallsAsync(BlocwerkDbContext db, Guid userId)
    {
        var walls = db.Walls.IgnoreQueryFilters();
        var boulders = db.Boulders.IgnoreQueryFilters();
        var footprint = db.WallMembers.Where(m => m.UserId == userId).Select(m => m.WallId)
            .Concat(walls.Where(w => w.OwnerId == userId || w.MaintenanceByUserId == userId || w.StagedByUserId == userId).Select(w => w.Id))
            .Concat(db.WallPanels.Where(p => p.StagedByUserId == userId).Select(p => p.WallId))
            .Concat(boulders.Where(b => b.CreatedByUserId == userId).Select(b => b.WallId))
            .Concat(db.BoulderComments.Where(c => c.UserId == userId).Select(c => c.Boulder.WallId))
            .Concat(db.Attempts.Where(a => a.UserId == userId).Select(a => a.Boulder.WallId))
            .Concat(db.BoulderRatings.Where(r => r.UserId == userId).Select(r => r.Boulder.WallId))
            .Concat(db.GradeProposals.Where(g => g.ProposedByUserId == userId).Select(g => g.Boulder.WallId))
            .Concat(db.BoulderSetters.Where(s => s.UserId == userId).Select(s => s.Boulder.WallId))
            .Concat(db.BetaVideos.Where(v => v.UploadedByUserId == userId).Select(v => v.Boulder.WallId))
            .Concat(db.ActivityLog.Where(a => a.UserId == userId).Select(a => a.WallId))
            .Concat(db.HoldLinks.Where(l => l.CreatedByUserId == userId).Select(l => l.WallId))
            .Concat(db.WallResets.Where(r => r.ResetByUserId == userId).Select(r => r.WallId));
        return await footprint.Distinct().ToListAsync(CancellationToken.None);
    }
}
