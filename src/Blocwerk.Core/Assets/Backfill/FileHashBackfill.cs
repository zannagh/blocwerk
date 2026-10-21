// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Diagnostics;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Assets.Backfill;

/// <summary>
/// Hashes the stored files of one <see cref="ContentHashFileColumn"/> where the hash is null: reads a batch of rows with
/// their row version (<c>xmin</c>), then streams each file through SHA-256 with no connection or lock held, and writes
/// the hash only if the row is still that version. A row rewritten meanwhile (the privacy pass replaces a gallery file in
/// place and moves the row in the same transaction) is skipped and left for a retry, never stamped with a stale hash.
/// </summary>
internal sealed class FileHashBackfill(
    Func<BlocwerkDbContext> createDb,
    IWallImageStorage images,
    ICaptureFileStore captures,
    ContentHashBackfillOptions options,
    ILogger logger)
{
    public async Task<BackfillCount> RunAsync(ContentHashFileColumn column, CancellationToken ct)
    {
        var pacer = new BackfillPacer(options);
        var progress = new BackfillProgress(logger, $"{column.Entity.Name}.{column.Path}", options.ProgressInterval);
        var total = BackfillCount.None;
        var after = Guid.Empty;
        while (await ReadAsync(column, after, ct) is { Count: > 0 } rows)
        {
            foreach (var row in rows)
            {
                var started = Stopwatch.GetTimestamp();
                total += await StampAsync(column, row, ct);
                progress.Report(total);
                await pacer.PauseAsync(Stopwatch.GetElapsedTime(started), ct);
            }

            after = rows[^1].Id;
        }

        progress.Done(total);
        return total;
    }

    /// <summary>The absolute path of a stored file, or null when the name escapes its store.</summary>
    internal static string? Resolve(IWallImageStorage images, ICaptureFileStore captures, bool inCaptureStore, string stored) =>
        inCaptureStore ? captures.ResolvePhysicalPath(stored) : images.ResolvePhysicalPath(stored);

    /// <summary>
    /// The file's hash; null when it is missing or cannot be read (a directory in its place, no permission, an I/O error).
    /// An unreadable file is logged and counted as missing, so one bad row never ends the run (and every restart after).
    /// </summary>
    internal static async Task<byte[]?> TryHashAsync(string? path, ILogger logger, CancellationToken ct)
    {
        try
        {
            return await ContentHash.OfFileAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Content-hash backfill could not read {Path}: {Reason}", path, ex.Message);
            return null;
        }
    }

    /// <summary>The file's length for the progress log; 0 when it is not there.</summary>
    internal static long SizeOf(string? path)
    {
        var file = string.IsNullOrEmpty(path) ? null : new FileInfo(path);
        return file is { Exists: true } ? file.Length : 0;
    }

    private async Task<List<BackfillGuardedRow>> ReadAsync(ContentHashFileColumn column, Guid after, CancellationToken ct)
    {
        await using var db = createDb();
        var sql = ContentHashBackfillSql.For(db, column.Entity);
        return await db.Database.SqlQueryRaw<BackfillGuardedRow>(sql.SelectFiles(column), after, options.FileBatch).ToListAsync(ct);
    }

    private async Task<BackfillCount> StampAsync(ContentHashFileColumn column, BackfillGuardedRow row, CancellationToken ct)
    {
        var path = Resolve(images, captures, column.InCaptureStore, row.Value);
        var size = SizeOf(path);
        var hash = await TryHashAsync(path, logger, ct);
        if (hash is null)
        {
            logger.LogDebug("Content-hash backfill: {Column} of {Id} has no readable file at {Stored}", column.Path, row.Id, row.Value);
            return new BackfillCount(0, 0, 0, 1);
        }

        await using var db = createDb();
        var sql = ContentHashBackfillSql.For(db, column.Entity);
        var written = await db.Database.ExecuteSqlRawAsync(sql.StampFile(column), [hash, row.Id, row.Token], ct);
        return written == 1 ? new BackfillCount(1, size, 0, 0) : new BackfillCount(0, 0, 1, 0);
    }
}
