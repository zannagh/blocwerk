// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Diagnostics;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Assets.Backfill;

/// <summary>
/// Fills the <c>sha</c> (lowercase hex) of every level-of-detail entry in <see cref="WallGeometrySplat.LodLevelsJson"/>
/// that has none: the level files are streamed through SHA-256 with no lock held, and the JSON is replaced only if the
/// row is still the version that was read (<c>xmin</c>). Levels whose file is missing keep a null <c>sha</c>.
/// </summary>
internal sealed class LadderHashBackfill
{
    private readonly Func<BlocwerkDbContext> createDb;
    private readonly ICaptureFileStore captures;
    private readonly ContentHashBackfillOptions options;
    private readonly ILogger logger;

    public LadderHashBackfill(Func<BlocwerkDbContext> createDb, ICaptureFileStore captures, ContentHashBackfillOptions options, ILogger logger)
    {
        this.createDb = createDb;
        this.captures = captures;
        this.options = options;
        this.logger = logger;
    }

    public async Task<BackfillCount> RunAsync(CancellationToken ct)
    {
        var pacer = new BackfillPacer(options);
        var progress = new BackfillProgress(logger, $"{nameof(WallGeometrySplat)}.{nameof(WallGeometrySplat.LodLevelsJson)}", options.ProgressInterval);
        var total = BackfillCount.None;
        var after = Guid.Empty;
        while (await ReadAsync(after, ct) is { Count: > 0 } rows)
        {
            foreach (var row in rows)
            {
                var started = Stopwatch.GetTimestamp();
                total += await StampAsync(row, ct);
                progress.Report(total);
                await pacer.PauseAsync(Stopwatch.GetElapsedTime(started), ct);
            }

            after = rows[^1].Id;
        }

        progress.Done(total);
        return total;
    }

    private async Task<List<BackfillGuardedRow>> ReadAsync(Guid after, CancellationToken ct)
    {
        await using var db = createDb();
        var sql = ContentHashBackfillSql.For(db, typeof(WallGeometrySplat));
        return await db.Database
            .SqlQueryRaw<BackfillGuardedRow>(sql.SelectJson(nameof(WallGeometrySplat.LodLevelsJson)), after, options.FileBatch)
            .ToListAsync(ct);
    }

    /// <summary>Hashes the row's unhashed levels; one stamped row per row whose JSON was replaced.</summary>
    private async Task<BackfillCount> StampAsync(BackfillGuardedRow row, CancellationToken ct)
    {
        var levels = SplatLodLadder.Parse(row.Value);
        if (levels.All(l => l.Sha is not null))
        {
            return BackfillCount.None;
        }

        var (hashed, bytes, missing) = (new List<SplatLodLevel>(levels.Count), 0L, 0);
        foreach (var level in levels)
        {
            var path = level.Sha is null ? captures.ResolvePhysicalPath(level.StoredPath) : null;
            var sha = level.Sha ?? ContentHash.ToHex(await ContentHash.OfFileAsync(path, ct));
            missing += sha is null ? 1 : 0;
            bytes += level.Sha is null && sha is not null ? FileHashBackfill.SizeOf(path) : 0;
            hashed.Add(level with { Sha = sha });
        }

        if (hashed.SequenceEqual(levels))
        {
            return new BackfillCount(0, 0, 0, missing);
        }

        await using var db = createDb();
        var sql = ContentHashBackfillSql.For(db, typeof(WallGeometrySplat));
        var written = await db.Database.ExecuteSqlRawAsync(
            sql.StampJson(nameof(WallGeometrySplat.LodLevelsJson)), [SplatLodLadder.Serialize(hashed), row.Id, row.Token], ct);
        return written == 1 ? new BackfillCount(1, bytes, 0, missing) : new BackfillCount(0, 0, 1, 0);
    }
}
