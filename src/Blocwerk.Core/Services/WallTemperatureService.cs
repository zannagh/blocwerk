using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <inheritdoc cref="IWallTemperatureService"/>
public class WallTemperatureService : IWallTemperatureService
{
    /// <summary>Most buckets a single aggregate may return; a chart never needs more.</summary>
    public const int MaxBuckets = 2000;

    /// <summary>Most raw readings a single read may return, whatever window it spans.</summary>
    public const int MaxReadings = 10000;

    /// <summary>A client-stamped sample may run this far ahead of the server clock.</summary>
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>No wall existed before this, so an older timestamp is a broken clock, not history.</summary>
    private static readonly DateTimeOffset EarliestPlausibleReading = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ILogger<WallTemperatureService> logger;

    public WallTemperatureService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ILogger<WallTemperatureService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.logger = logger;
    }

    public async Task<WallTemperatureReading> RecordReadingAsync(
        Guid wallId,
        double temperatureCelsius,
        DateTimeOffset? recordedAt = null,
        CancellationToken ct = default)
    {
        // A sensor that only posts a value gets the server clock; one that reports when it measured
        // is taken at its word, but only within a window a working clock could produce.
        var stamp = recordedAt is { } supplied ? Validated(supplied) : DateTimeOffset.UtcNow;

        // The caller is a machine holding a wall-scoped key, so there is no user context to filter by.
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        var wallExists = await db.Walls.IgnoreQueryFilters().AnyAsync(w => w.Id == wallId, ct);
        if (!wallExists)
        {
            logger.LogWarning("Temperature reading rejected: wall {WallId} not found", wallId);
            throw new InvalidOperationException("Wall not found");
        }

        var reading = new WallTemperatureReading
        {
            WallId = wallId,
            TemperatureCelsius = temperatureCelsius,
            RecordedAt = stamp,
        };

        db.WallTemperatureReadings.Add(reading);
        await db.SaveChangesAsync(ct);
        return reading;
    }

    public async Task<WallTemperaturePage> GetReadingsAsync(
        Guid wallId,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken ct = default)
    {
        if (limit < 1 || limit > MaxReadings)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                $"At most {MaxReadings} raw readings are returned per call.");
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        // The sensor posts roughly once a second, so an unbounded window is millions of rows and
        // gigabytes of allocation. Read newest-first up to one row past the cap: that extra row is
        // how the caller learns the window held more than it was given.
        var newestFirst = await db.WallTemperatureReadings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.WallId == wallId && r.RecordedAt >= from && r.RecordedAt < to)
            .OrderByDescending(r => r.RecordedAt)
            .Take(limit + 1)
            .ToListAsync(ct);

        var truncated = newestFirst.Count > limit;
        if (truncated)
        {
            newestFirst.RemoveAt(newestFirst.Count - 1);
        }

        newestFirst.Reverse();
        return new WallTemperaturePage(newestFirst, truncated);
    }

    public async Task<IReadOnlyList<WallTemperatureBucket>> GetBucketedReadingsAsync(
        Guid wallId,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan bucketWidth,
        CancellationToken ct = default)
    {
        GuardBucketRequest(from, to, bucketWidth);

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        var rows = IsSqlite(db)
            ? await SqliteBucketQuery(db, wallId, from, to, bucketWidth).ToListAsync(ct)
            : await BucketQuery(db, wallId, from, to, bucketWidth).ToListAsync(ct);

        return rows
            .Select(r => new WallTemperatureBucket(
                from + TimeSpan.FromTicks(bucketWidth.Ticks * r.Bucket),
                r.Average,
                r.Minimum,
                r.Maximum,
                r.Samples))
            .ToList();
    }

    public async Task<WallTemperatureReading?> GetLatestReadingAsync(Guid wallId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        return await db.WallTemperatureReadings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.WallId == wallId)
            .OrderByDescending(r => r.RecordedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<DateTimeOffset?> GetEarliestReadingAtAsync(Guid wallId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        var oldest = await db.WallTemperatureReadings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.WallId == wallId)
            .OrderBy(r => r.RecordedAt)
            .FirstOrDefaultAsync(ct);

        return oldest?.RecordedAt;
    }

    /// <summary>
    /// The bucket aggregate as a single GROUP BY, computed entirely by the database. The key is the
    /// whole number of bucket widths between <paramref name="from"/> and the reading, which Npgsql
    /// renders as <c>CAST(floor(date_part('epoch', "RecordedAt" - @from) / @width) AS bigint)</c>:
    /// one round trip and one row per non-empty bucket, whatever the size of the window.
    /// </summary>
    internal static IQueryable<TemperatureBucketRow> BucketQuery(
        BlocwerkDbContext db,
        Guid wallId,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan bucketWidth)
    {
        var widthSeconds = bucketWidth.TotalSeconds;
        return db.WallTemperatureReadings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.WallId == wallId && r.RecordedAt >= from && r.RecordedAt < to)
            .GroupBy(r => (long)Math.Floor((r.RecordedAt - from).TotalSeconds / widthSeconds))
            .OrderBy(g => g.Key)
            .Select(g => new TemperatureBucketRow(
                g.Key,
                g.Average(r => r.TemperatureCelsius),
                g.Min(r => r.TemperatureCelsius),
                g.Max(r => r.TemperatureCelsius),
                g.Count()));
    }

    /// <summary>
    /// The same aggregate for SQLite, which the tests run on. SQLite translates no arithmetic on a
    /// <see cref="DateTimeOffset"/> whatsoever, so the identical GROUP BY is spelled out against the
    /// tick column its model stores. Every value is a parameter, never interpolated text.
    /// </summary>
    internal static IQueryable<TemperatureBucketRow> SqliteBucketQuery(
        BlocwerkDbContext db,
        Guid wallId,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan bucketWidth)
    {
        return db.Database.SqlQueryRaw<TemperatureBucketRow>(
            @"SELECT (""RecordedAt"" - {0}) / {1} AS ""Bucket"",
                     AVG(""TemperatureCelsius"") AS ""Average"",
                     MIN(""TemperatureCelsius"") AS ""Minimum"",
                     MAX(""TemperatureCelsius"") AS ""Maximum"",
                     COUNT(*) AS ""Samples""
              FROM ""WallTemperatureReadings""
              WHERE ""WallId"" = {2} AND ""RecordedAt"" >= {0} AND ""RecordedAt"" < {3}
              GROUP BY ""Bucket""
              ORDER BY ""Bucket""",
            from.UtcTicks,
            bucketWidth.Ticks,
            wallId,
            to.UtcTicks);
    }

    private static bool IsSqlite(BlocwerkDbContext db) =>
        db.Database.ProviderName?.EndsWith(".Sqlite", StringComparison.Ordinal) == true;

    /// <summary>Keeps a caller from asking for a million buckets, or for a window that runs backwards.</summary>
    private static void GuardBucketRequest(DateTimeOffset from, DateTimeOffset to, TimeSpan bucketWidth)
    {
        if (bucketWidth <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bucketWidth),
                bucketWidth,
                "The bucket width must be positive.");
        }

        if (to <= from)
        {
            throw new ArgumentOutOfRangeException(nameof(to), to, "'from' must be earlier than 'to'.");
        }

        var buckets = ((to - from).Ticks + bucketWidth.Ticks - 1) / bucketWidth.Ticks;
        if (buckets > MaxBuckets)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bucketWidth),
                bucketWidth,
                $"That window would produce {buckets} buckets; at most {MaxBuckets} are returned.");
        }
    }

    /// <summary>Accepts a client-supplied timestamp only when a working clock could have produced it.</summary>
    private static DateTimeOffset Validated(DateTimeOffset recordedAt)
    {
        var stamp = recordedAt.ToUniversalTime();
        if (stamp > DateTimeOffset.UtcNow + MaxClockSkew)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recordedAt),
                recordedAt,
                "The timestamp lies in the future.");
        }

        if (stamp < EarliestPlausibleReading)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recordedAt),
                recordedAt,
                "The timestamp is implausibly old.");
        }

        return stamp;
    }
}
