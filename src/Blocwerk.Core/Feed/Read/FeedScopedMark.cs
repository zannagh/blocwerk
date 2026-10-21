// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The resume mark an outsider (share link, kiosk) is handed instead of the global high-water mark. The global mark moves
/// with every wall's writes, so a share viewer polling a quiet wall would watch other walls' activity through it. This one
/// is the newest row the viewer's own filter matches at or below the global mark, or the retention floor if that is
/// higher (so a wall quiet for longer than retention does not resync on every read).
/// </summary>
/// <remarks>
/// It is as sound as the global mark: a short page has read every row of the filter up to the ceiling, the global mark is
/// the newest stored row under the ceiling, so no row of the filter lies between this mark and the ceiling, and the next
/// read from it re-reads nothing. The floor and the epoch are global, but they move only with retention and restores.
/// </remarks>
internal static class FeedScopedMark
{
    public static async Task<FeedCursor> OfAsync(
        BlocwerkDbContext db, FeedFilter filter, FeedCursor global, CancellationToken cancellationToken)
    {
        var horizon = FeedHorizons.For(db);
        var newest = await horizon.InOrder(horizon.Between(db.WallChanges.AsNoTracking().Where(filter.Predicate), FeedPosition.Start, global.Position))
            .Select(c => new { c.TxId, c.Seq })
            .LastOrDefaultAsync(cancellationToken);
        var stored = newest is null
            ? new FeedPosition(global.Position.TxId is null ? null : 0, 0)
            : new FeedPosition(newest.TxId, newest.Seq);
        var epoch = await FeedEpochStore.ReadAsync(db, cancellationToken);
        var floor = epoch is { FloorSeq: > 0 } ? new FeedPosition(epoch.FloorTxId, epoch.FloorSeq) : FeedPosition.Start;
        return new FeedCursor(global.Epoch, FeedPosition.Max(stored, floor));
    }
}
