// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Diagnostics;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Assets.Backfill;

/// <summary>
/// Hashes one database blob column (<see cref="ContentHashColumn"/>) where the hash is null, in bounded batches by id.
/// </summary>
/// <remarks>
/// On Postgres the bytes never leave the database: one statement locks a batch (<c>FOR NO KEY UPDATE SKIP LOCKED</c>, so
/// it neither waits on a writer nor blocks the foreign-key checks of inserts that reference the row) and sets
/// <c>sha256(bytes)</c> from the very row version it locked, so the hash cannot be stale. A row a writer holds is skipped
/// and counted for a retry. Raw SQL never reaches the change tracker, so the hash stamp, the change journal and the feed
/// see none of it. SQLite (tests) has no <c>sha256</c>: the app hashes the batch and writes each hash only while the row
/// still holds the bytes it hashed.
/// </remarks>
internal sealed class BlobHashBackfill(
    Func<BlocwerkDbContext> createDb,
    ContentHashBackfillOptions options,
    ILogger logger)
{
    public async Task<BackfillCount> RunAsync(ContentHashColumn column, CancellationToken ct)
    {
        var pacer = new BackfillPacer(options);
        var progress = new BackfillProgress(logger, $"{column.Entity.Name}.{column.Bytes}", options.ProgressInterval);
        var total = BackfillCount.None;
        var after = Guid.Empty;
        while (true)
        {
            var started = Stopwatch.GetTimestamp();
            var (batch, last) = await BatchAsync(column, after, pacer.BatchSize, ct);
            if (last is not { } next)
            {
                break;
            }

            after = next;
            total += batch;
            progress.Report(total);
            var took = Stopwatch.GetElapsedTime(started);
            pacer.Measure(took);
            await pacer.PauseAsync(took, ct);
        }

        total = await WithPendingAsync(column, total, ct);
        progress.Done(total);
        return total;
    }

    private async Task<(BackfillCount Count, Guid? Last)> BatchAsync(ContentHashColumn column, Guid after, int size, CancellationToken ct)
    {
        await using var db = createDb();
        var sql = ContentHashBackfillSql.For(db, column.Entity);
        if (sql.Postgres)
        {
            var stamped = await db.Database.SqlQueryRaw<BackfillStampedRow>(sql.StampBlobs(column), after, size, options.MaxBlobBatchBytes).ToListAsync(ct);
            return stamped.Count == 0
                ? (BackfillCount.None, null)
                : (new BackfillCount(stamped.Count, stamped.Sum(r => r.Size), 0, 0), stamped[^1].Id);
        }

        var rows = await db.Database.SqlQueryRaw<BackfillBlobRow>(sql.SelectBlobs(column), after, size, options.MaxBlobBatchBytes).ToListAsync(ct);
        var count = BackfillCount.None;
        foreach (var row in rows)
        {
            var written = await db.Database.ExecuteSqlRawAsync(sql.StampBlob(column), [ContentHash.Of(row.Bytes)!, row.Id, row.Bytes], ct);
            count += written == 1 ? new BackfillCount(1, row.Bytes.LongLength, 0, 0) : new BackfillCount(0, 0, 1, 0);
        }

        return (count, rows.Count == 0 ? null : rows[^1].Id);
    }

    /// <summary>On Postgres the rows a writer held were skipped silently: whatever is still unhashed is left for a retry.</summary>
    private async Task<BackfillCount> WithPendingAsync(ContentHashColumn column, BackfillCount total, CancellationToken ct)
    {
        await using var db = createDb();
        var sql = ContentHashBackfillSql.For(db, column.Entity);
        if (!sql.Postgres)
        {
            return total;
        }

        var pending = await db.Database.SqlQueryRaw<int>(sql.CountPending(column)).SingleAsync(ct);
        return total with { Retry = pending };
    }
}
