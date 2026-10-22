// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Mine;
using Blocwerk.Core.Assets.DataDocs;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.Mine;

/// <summary>
/// Serves <c>GET /api/me/walls/{w}/mine</c> (Phase 3 plan §1, PR 3.4): the reader's own ticks and favourites of one
/// wall, for the boulders their class sees in the <c>boulders</c> document (drafts and archived ones for members and
/// setters), and the feed mark read <b>before</b> them, so a tick logged during the read is replayed, never lost.
/// </summary>
/// <remarks>
/// Built per request and never stored: the marks are one reader's, unlike the class-wide data documents. Only the
/// reader's own rows are read (<c>UserId</c> is the gate's, never a parameter of the request).
/// </remarks>
public sealed class MineDocumentService(RootDbContextFactory dbContextFactory, IWallFeed feed)
{
    /// <summary>The reader and their class on <paramref name="wallId"/>, or why there is none.</summary>
    public async Task<MineAccess> AccessAsync(Guid wallId, FeedSession session, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        return await MineAccessGate.DecideAsync(db, wallId, session, ct);
    }

    /// <summary>The granted reader's marks on the wall, labelled with the caller's cache <paramref name="partition"/>.</summary>
    public async Task<MineDocument> BuildAsync(Guid wallId, Guid userId, DocumentAudience audience, string partition, CancellationToken ct)
    {
        var mark = await feed.CurrentAsync(ct);
        await using var db = dbContextFactory.CreateDbContext();
        var drafts = DocumentAudiences.SeesDrafts(audience);
        var archived = DocumentAudiences.SeesArchived(audience);
        var boulders = db.Boulders.AsNoTracking()
            .Where(b => b.WallId == wallId && (archived || !b.IsArchived) && (drafts || !b.IsDraft))
            .Select(b => b.Id);
        var attempts = await db.Attempts.AsNoTracking()
            .Where(a => a.UserId == userId && boulders.Contains(a.BoulderId))
            .Select(a => new { a.BoulderId, a.Type, a.Timestamp })
            .ToListAsync(ct);
        var favourites = await db.BoulderFavorites.AsNoTracking()
            .Where(f => f.UserId == userId && boulders.Contains(f.BoulderId))
            .Select(f => f.BoulderId)
            .ToListAsync(ct);

        // Grouped here, not in SQL: a projection of a group does not translate on every provider.
        var ticks = attempts.GroupBy(a => a.BoulderId)
            .OrderBy(g => g.Key)
            .Select(g => new MineDocumentTick(
                g.Key,
                g.Count(),
                g.Any(a => a.Type == AttemptType.Send),
                g.Any(a => a.Type == AttemptType.Flash),
                g.Max(a => a.Timestamp)))
            .ToList();
        return new MineDocument(DataDocumentSchema.Version, wallId, partition, mark.ToString(), ticks, favourites.Order().ToList());
    }
}
