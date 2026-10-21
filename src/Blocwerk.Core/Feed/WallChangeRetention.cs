// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Deletes feed rows older than the retention window and raises the epoch's floor to the newest deleted position, so a
/// cursor that may have missed a deleted row is answered with a resync. One floor for the whole feed: a client that has
/// not caught up within the window reloads, whichever wall it follows.
/// </summary>
public static class WallChangeRetention
{
    /// <summary>How long rows are kept (decided: 30 days).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <summary>Deletes the rows created before <paramref name="cutoff"/> and returns how many went.</summary>
    public static async Task<int> RunAsync(BlocwerkDbContext db, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var horizon = FeedHorizons.For(db);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var epoch = await db.FeedEpochs.OrderBy(e => e.StartedAt).FirstOrDefaultAsync(cancellationToken);
        if (epoch is null)
        {
            return 0;
        }

        // Only final rows (below the ceiling), and only up to the newest one found here: a row that commits meanwhile with
        // an old CreatedAt stays for the next run instead of being deleted above the floor.
        var ceiling = await horizon.CeilingAsync(db, cancellationToken);
        var doomed = horizon.Between(db.WallChanges.Where(c => c.CreatedAt < cutoff), FeedPosition.Start, ceiling);
        var newest = await horizon.InOrder(doomed).Select(c => new { c.TxId, c.Seq }).LastOrDefaultAsync(cancellationToken);
        if (newest is null)
        {
            return 0;
        }

        var floor = new FeedPosition(newest.TxId, newest.Seq);
        var deleted = await horizon.Between(doomed, FeedPosition.Start, floor).ExecuteDeleteAsync(cancellationToken);
        if (floor > new FeedPosition(epoch.FloorTxId, epoch.FloorSeq))
        {
            epoch.FloorTxId = floor.TxId;
            epoch.FloorSeq = floor.Seq;
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }
}
