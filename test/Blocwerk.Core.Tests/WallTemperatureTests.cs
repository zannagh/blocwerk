using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Covers the temperature series: the database-side bucket aggregate that feeds the wall chart,
/// the "how far back does this go" probe, and the client-supplied timestamp on a write.
/// </summary>
public class WallTemperatureTests
{
    private static readonly DateTimeOffset Origin = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetBucketedReadings_AveragesEachBucket_AndReportsItsExtremes()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        // Two buckets of ten minutes: 1/3/5 in the first, 10/20 in the second.
        await SeedReadingsAsync(h, (0, 1), (2, 3), (9, 5), (10, 10), (19, 20));

        var buckets = await h.WallTemperatureService.GetBucketedReadingsAsync(
            h.WallId, Origin, Origin.AddMinutes(20), TimeSpan.FromMinutes(10));

        Assert.Equal(2, buckets.Count);

        Assert.Equal(Origin, buckets[0].BucketStart);
        Assert.Equal(3d, buckets[0].AverageCelsius, 6);
        Assert.Equal(1d, buckets[0].MinCelsius, 6);
        Assert.Equal(5d, buckets[0].MaxCelsius, 6);
        Assert.Equal(3, buckets[0].SampleCount);

        // The reading exactly on the boundary belongs to the later bucket, not the earlier one.
        Assert.Equal(Origin.AddMinutes(10), buckets[1].BucketStart);
        Assert.Equal(15d, buckets[1].AverageCelsius, 6);
        Assert.Equal(10d, buckets[1].MinCelsius, 6);
        Assert.Equal(20d, buckets[1].MaxCelsius, 6);
        Assert.Equal(2, buckets[1].SampleCount);
    }

    [Fact]
    public async Task GetReadings_CapsTheSampleCount_AndSaysSo()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        // The sensor posts about once a second, so an uncapped read of a wide window is millions
        // of rows: the cap is what stops one request from allocating gigabytes.
        await SeedReadingsAsync(h, (0, 1), (1, 2), (2, 3), (3, 4), (4, 5));

        var page = await h.WallTemperatureService.GetReadingsAsync(
            h.WallId, Origin, Origin.AddHours(1), 3);

        Assert.True(page.Truncated);
        Assert.Equal(3, page.Readings.Count);

        // The most recent samples survive, still oldest first.
        Assert.Equal([3d, 4d, 5d], page.Readings.Select(r => r.TemperatureCelsius));
    }

    [Fact]
    public async Task GetReadings_ReportsNoTruncation_WhenEverythingFits()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        await SeedReadingsAsync(h, (0, 1), (1, 2), (2, 3));

        var page = await h.WallTemperatureService.GetReadingsAsync(
            h.WallId, Origin, Origin.AddHours(1), 3);

        Assert.False(page.Truncated);
        Assert.Equal(3, page.Readings.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(WallTemperatureService.MaxReadings + 1)]
    public async Task GetReadings_RejectsALimitOutsideTheCap(int limit)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => h.WallTemperatureService.GetReadingsAsync(h.WallId, Origin, Origin.AddHours(1), limit));
    }

    [Fact]
    public async Task GetBucketedReadings_TreatsTheWindowAsHalfOpen()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        await SeedReadingsAsync(h, (-1, 100), (0, 4), (9, 6), (10, 200));

        var buckets = await h.WallTemperatureService.GetBucketedReadingsAsync(
            h.WallId, Origin, Origin.AddMinutes(10), TimeSpan.FromMinutes(10));

        // 'from' is included, 'to' is not, so neither of the two outliers reaches the average.
        var only = Assert.Single(buckets);
        Assert.Equal(5d, only.AverageCelsius, 6);
        Assert.Equal(2, only.SampleCount);
    }

    [Fact]
    public async Task GetBucketedReadings_OmitsBucketsWithoutAnyReading()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        // Nothing at all in the second and third bucket: the sensor was offline.
        await SeedReadingsAsync(h, (1, 12), (31, 14));

        var buckets = await h.WallTemperatureService.GetBucketedReadingsAsync(
            h.WallId, Origin, Origin.AddMinutes(40), TimeSpan.FromMinutes(10));

        Assert.Equal(2, buckets.Count);
        Assert.Equal(Origin, buckets[0].BucketStart);
        Assert.Equal(Origin.AddMinutes(30), buckets[1].BucketStart);
    }

    [Fact]
    public async Task GetBucketedReadings_IgnoresOtherWalls()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        await SeedReadingsAsync(h, (1, 12));
        await SeedReadingsAsync(h, await SeedSecondWallAsync(h), (2, 40));

        var buckets = await h.WallTemperatureService.GetBucketedReadingsAsync(
            h.WallId, Origin, Origin.AddMinutes(10), TimeSpan.FromMinutes(10));

        Assert.Equal(12d, Assert.Single(buckets).AverageCelsius, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public async Task GetBucketedReadings_RejectsANonPositiveBucketWidth(int seconds)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            h.WallTemperatureService.GetBucketedReadingsAsync(
                h.WallId, Origin, Origin.AddHours(1), TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task GetBucketedReadings_RejectsAWindowThatWouldReturnTooManyBuckets()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        // A year of one-second buckets is 31 million rows of result; the cap stops it at the door.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            h.WallTemperatureService.GetBucketedReadingsAsync(
                h.WallId, Origin, Origin.AddDays(365), TimeSpan.FromSeconds(1)));

        // The cap itself is fine.
        var width = TimeSpan.FromTicks(TimeSpan.FromDays(365).Ticks / WallTemperatureService.MaxBuckets);
        var buckets = await h.WallTemperatureService.GetBucketedReadingsAsync(
            h.WallId, Origin, Origin.AddDays(365), width);
        Assert.Empty(buckets);
    }

    [Fact]
    public async Task GetBucketedReadings_RejectsAnInvertedWindow()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            h.WallTemperatureService.GetBucketedReadingsAsync(
                h.WallId, Origin, Origin.AddHours(-1), TimeSpan.FromMinutes(1)));
    }

    /// <summary>
    /// The aggregate the tests exercise runs on SQLite; production runs on PostgreSQL. This pins
    /// the shape of the query the PostgreSQL provider generates so a refactor cannot quietly turn
    /// it into a client-side evaluation that drags millions of rows into memory.
    /// </summary>
    [Fact]
    public void BucketQuery_OnPostgres_IsASingleServerSideGroupBy()
    {
        var options = new DbContextOptionsBuilder<BlocwerkDbContext>()
            .UseNpgsql("Host=localhost;Database=blocwerk;Username=blocwerk;Password=blocwerk")
            .Options;
        using var db = new BlocwerkDbContext(options);

        var sql = WallTemperatureService
            .BucketQuery(db, Guid.NewGuid(), Origin, Origin.AddDays(365), TimeSpan.FromHours(3))
            .ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.Ordinal);
        Assert.Contains("date_part('epoch'", sql, StringComparison.Ordinal);
        Assert.Contains("avg(", sql, StringComparison.Ordinal);
        Assert.Contains("min(", sql, StringComparison.Ordinal);
        Assert.Contains("max(", sql, StringComparison.Ordinal);
        Assert.Contains("count(*)", sql, StringComparison.Ordinal);

        // The table is read exactly once, and the window bounds travel as parameters rather than
        // as inlined text.
        Assert.Equal(1, sql.Split("FROM \"WallTemperatureReadings\"").Length - 1);
        Assert.Contains("@", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetEarliestReadingAt_ReportsTheOldestSample_AndNullWithoutAny()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        Assert.Null(await h.WallTemperatureService.GetEarliestReadingAtAsync(h.WallId));

        await SeedReadingsAsync(h, (30, 12), (5, 11), (90, 13));

        var earliest = await h.WallTemperatureService.GetEarliestReadingAtAsync(h.WallId);
        Assert.Equal(Origin.AddMinutes(5), earliest);
    }

    [Fact]
    public async Task RecordReading_WithoutATimestamp_StampsTheServerClock()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        var before = DateTimeOffset.UtcNow;
        var reading = await h.WallTemperatureService.RecordReadingAsync(h.WallId, 21.5);

        Assert.InRange(reading.RecordedAt, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task RecordReading_WithATimestamp_StoresItVerbatim()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        var measuredAt = DateTimeOffset.UtcNow.AddHours(-3).AddMinutes(-17);
        await h.WallTemperatureService.RecordReadingAsync(h.WallId, 19.25, measuredAt);

        var stored = await h.WallTemperatureService.GetLatestReadingAsync(h.WallId);
        Assert.NotNull(stored);
        Assert.Equal(measuredAt.ToUniversalTime(), stored.RecordedAt);
        Assert.Equal(19.25, stored.TemperatureCelsius);
    }

    [Fact]
    public async Task RecordReading_WithAnImplausibleTimestamp_IsRejected()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            h.WallTemperatureService.RecordReadingAsync(h.WallId, 20, DateTimeOffset.UtcNow.AddHours(1)));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            h.WallTemperatureService.RecordReadingAsync(h.WallId, 20, new DateTimeOffset(2011, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.Null(await h.WallTemperatureService.GetLatestReadingAsync(h.WallId));
    }

    private static async Task<Guid> SeedSecondWallAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var wall = new Wall { Name = "Other Wall", OwnerId = h.Owner.Id };
        db.Walls.Add(wall);
        await db.SaveChangesAsync();
        return wall.Id;
    }

    private static Task SeedReadingsAsync(WallTestHarness h, params (int Minutes, double Celsius)[] readings) =>
        SeedReadingsAsync(h, h.WallId, readings);

    private static async Task SeedReadingsAsync(
        WallTestHarness h,
        Guid wallId,
        params (int Minutes, double Celsius)[] readings)
    {
        await using var db = h.CreateContext();
        foreach (var (minutes, celsius) in readings)
        {
            db.WallTemperatureReadings.Add(new WallTemperatureReading
            {
                WallId = wallId,
                TemperatureCelsius = celsius,
                RecordedAt = Origin.AddMinutes(minutes),
            });
        }

        await db.SaveChangesAsync();
    }
}
