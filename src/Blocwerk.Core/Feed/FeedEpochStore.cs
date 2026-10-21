// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Creates the feed's epoch row and, on Postgres, starts a new epoch when the stored transaction ids come from another
/// cluster's xid space (after a dump and restore: prod was moved by <c>pg_restore</c> on 2026-09-02). Such ids would
/// compare wrongly against the new cluster's, so the feed is truncated and every old cursor is answered with a resync.
/// </summary>
public static class FeedEpochStore
{
    /// <summary>SQLite's fixed epoch: it has no xid space to be restored out of.</summary>
    public static readonly Guid SqliteEpoch = new("5c7e1b0a-2f4d-4e0b-9a1d-7f3c8e6b2a10");

    // Any stored id at or above this transaction's own xid cannot have been issued by this cluster. The floor counts too:
    // retention may have deleted every row, and no cursor is ever handed out above max(stored, floor).
    private const string ForeignTxIdsSql =
        "SELECT GREATEST(COALESCE((SELECT MAX(\"TxId\") FROM \"WallChanges\"), 0), "
        + "COALESCE((SELECT MAX(\"FloorTxId\") FROM \"FeedEpochs\"), 0)) >= pg_current_xact_id()::text::bigint AS \"Value\"";

    /// <summary>Returns the current epoch, creating it, or replacing it after a restore (Postgres), as needed.</summary>
    public static Task<FeedEpoch> EnsureAsync(BlocwerkDbContext db, ILogger logger, CancellationToken cancellationToken) =>
        db.Database.IsNpgsql() ? EnsurePostgresAsync(db, logger, cancellationToken) : EnsureSqliteAsync(db, cancellationToken);

    /// <summary>The current epoch row, or null before <see cref="EnsureAsync"/> first ran.</summary>
    public static Task<FeedEpoch?> ReadAsync(BlocwerkDbContext db, CancellationToken cancellationToken) =>
        db.FeedEpochs.AsNoTracking().OrderBy(e => e.StartedAt).FirstOrDefaultAsync(cancellationToken);

    private static async Task<FeedEpoch> EnsurePostgresAsync(BlocwerkDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Serialises concurrent checks; plain reads of the epoch (ACCESS SHARE) are not blocked.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"FeedEpochs\" IN EXCLUSIVE MODE", cancellationToken);
        var current = await db.FeedEpochs.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var foreign = await db.Database.SqlQueryRaw<bool>(ForeignTxIdsSql).SingleAsync(cancellationToken);
        if (current is not null && !foreign)
        {
            await transaction.CommitAsync(cancellationToken);
            return current;
        }

        if (foreign)
        {
            logger.LogWarning(
                "The live-update feed holds transaction ids from another cluster (a restore?). Truncating it and starting a new epoch; connected clients resync.");
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"WallChanges\"", cancellationToken);
        }

        await db.FeedEpochs.ExecuteDeleteAsync(cancellationToken);
        var next = new FeedEpoch();
        db.FeedEpochs.Add(next);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return next;
    }

    private static async Task<FeedEpoch> EnsureSqliteAsync(BlocwerkDbContext db, CancellationToken cancellationToken)
    {
        if (await ReadAsync(db, cancellationToken) is { } current)
        {
            return current;
        }

        var epoch = new FeedEpoch { Id = SqliteEpoch };
        db.FeedEpochs.Add(epoch);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return epoch;
        }
        catch (DbUpdateException)
        {
            // A concurrent caller inserted the same fixed row first.
            db.Entry(epoch).State = EntityState.Detached;
            return await ReadAsync(db, cancellationToken) ?? throw new InvalidOperationException("The feed epoch row vanished.");
        }
    }
}
