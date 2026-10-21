// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Creates the feed's epoch row and, on Postgres, starts a new epoch after a restore or copy (prod was moved by
/// <c>pg_restore</c> on 2026-09-02): when the database identity differs from the stored one (a restore into a new
/// database gets a new oid, a new cluster a new system identifier), or when stored transaction ids come from another
/// cluster's xid space. Such ids would compare wrongly against the new cluster's, so the feed is truncated and every old
/// cursor is answered with a resync. The identity check also covers a live dump, whose later rows the restore lost.
/// </summary>
public static class FeedEpochStore
{
    /// <summary>SQLite's fixed epoch: it has no xid space to be restored out of.</summary>
    public static readonly Guid SqliteEpoch = new("5c7e1b0a-2f4d-4e0b-9a1d-7f3c8e6b2a10");

    // Every xid this cluster has issued is below the snapshot's xmax, so a stored id at or above it came from another
    // cluster. Unlike the transaction's own xid, xmax does not depend on when this transaction got its xid, and reading
    // it burns none. The floor counts too: retention may have deleted every row, and no cursor is ever handed out above
    // max(stored, floor).
    private const string ForeignTxIdsSql =
        "SELECT GREATEST(COALESCE((SELECT MAX(\"TxId\") FROM \"WallChanges\"), 0), "
        + "COALESCE((SELECT MAX(\"FloorTxId\") FROM \"FeedEpochs\"), 0)) >= pg_snapshot_xmax(pg_current_snapshot())::text::bigint AS \"Value\"";

    // A reset that waits on an open writer gives up instead of stalling startup; the feed retries on its first read.
    private const string LockTimeoutSql = "SET LOCAL lock_timeout = '5s'";

    /// <summary>
    /// Returns the current epoch, creating it, or replacing it after a restore (Postgres), as needed. Runs in its own
    /// transaction, or in the caller's open one (which the caller then commits).
    /// </summary>
    public static Task<FeedEpoch> EnsureAsync(BlocwerkDbContext db, ILogger logger, CancellationToken cancellationToken) =>
        db.Database.IsNpgsql() ? EnsurePostgresAsync(db, logger, cancellationToken) : EnsureSqliteAsync(db, cancellationToken);

    /// <summary>The current epoch row, or null before <see cref="EnsureAsync"/> first ran.</summary>
    public static Task<FeedEpoch?> ReadAsync(BlocwerkDbContext db, CancellationToken cancellationToken) =>
        db.FeedEpochs.AsNoTracking().OrderBy(e => e.StartedAt).FirstOrDefaultAsync(cancellationToken);

    private static async Task<FeedEpoch> EnsurePostgresAsync(BlocwerkDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        // Read outside the transaction: a refused pg_control_system() call must not abort it.
        var identity = await FeedDatabaseIdentity.ReadAsync(db, logger, cancellationToken);

        // Joins a transaction the caller already opened (it then commits it), or runs in its own.
        var owned = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var epoch = await CheckOrReplaceAsync(db, identity, logger, cancellationToken);
            if (owned is not null)
            {
                await owned.CommitAsync(cancellationToken);
            }

            return epoch;
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }
        }
    }

    private static async Task<FeedEpoch> CheckOrReplaceAsync(
        BlocwerkDbContext db, FeedDatabaseIdentity identity, ILogger logger, CancellationToken cancellationToken)
    {
        // Serialises concurrent checks; plain reads of the epoch (ACCESS SHARE) are not blocked.
        await db.Database.ExecuteSqlRawAsync(LockTimeoutSql, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"FeedEpochs\" IN EXCLUSIVE MODE", cancellationToken);
        var current = await db.FeedEpochs.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var foreign = await db.Database.SqlQueryRaw<bool>(ForeignTxIdsSql).SingleAsync(cancellationToken);
        var moved = current is not null && !identity.Matches(current);
        if (current is not null && !foreign && !moved)
        {
            return current;
        }

        // A replaced epoch keeps no rows: their transaction ids may belong to another cluster's xid space. The very first
        // epoch keeps them (rows written before it was created are this database's own).
        if (moved || foreign)
        {
            logger.LogWarning(
                "The live-update feed was restored or copied ({Reason}). Truncating it and starting a new epoch; connected clients resync.",
                moved ? "another database or cluster" : "transaction ids from another cluster");
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"WallChanges\"", cancellationToken);
        }

        await db.FeedEpochs.ExecuteDeleteAsync(cancellationToken);
        var next = new FeedEpoch { DatabaseOid = identity.DatabaseOid, SystemIdentifier = identity.SystemIdentifier };
        db.FeedEpochs.Add(next);
        await db.SaveChangesAsync(cancellationToken);
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
