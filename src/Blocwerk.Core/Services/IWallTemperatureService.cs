using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// Records and reads the temperature series of a wall. Writes come from sensors authenticating
/// with a wall-scoped API key, so the timestamp is stamped here rather than taken from the caller.
/// </summary>
public interface IWallTemperatureService
{
    /// <summary>
    /// Stores one sample for a wall. <paramref name="recordedAt"/> is null for the sensors that
    /// simply post a value — those are stamped with the current UTC time. A client that knows when
    /// it measured may supply the timestamp, which is stored as given (normalised to UTC) as long
    /// as it is plausible; implausible timestamps throw <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    Task<WallTemperatureReading> RecordReadingAsync(
        Guid wallId,
        double temperatureCelsius,
        DateTimeOffset? recordedAt = null,
        CancellationToken ct = default);

    /// <summary>
    /// Readings of a wall between <paramref name="from"/> (inclusive) and <paramref name="to"/>
    /// (exclusive), oldest first, capped at <paramref name="limit"/> rows. A wall's sensor posts
    /// about once a second, so the cap is not a nicety: without it a wide window is millions of
    /// rows in memory. When the window holds more than the cap the MOST RECENT
    /// <paramref name="limit"/> readings are returned and the result says so, so a caller can
    /// never mistake a truncated series for the whole one.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="limit"/> is below 1 or above <see cref="WallTemperatureService.MaxReadings"/>.
    /// </exception>
    Task<WallTemperaturePage> GetReadingsAsync(
        Guid wallId,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Averages the readings of a wall into fixed-width buckets between <paramref name="from"/>
    /// (inclusive) and <paramref name="to"/> (exclusive), oldest first. The aggregation runs in the
    /// database — a chart over a year must never drag tens of millions of rows into memory.
    /// Buckets without a single reading are absent from the result; a caller drawing a chart fills
    /// those gaps itself.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The bucket width is not positive, the window is inverted, or the window would produce more
    /// than <see cref="WallTemperatureService.MaxBuckets"/> buckets.
    /// </exception>
    Task<IReadOnlyList<WallTemperatureBucket>> GetBucketedReadingsAsync(
        Guid wallId,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan bucketWidth,
        CancellationToken ct = default);

    /// <summary>The most recent reading of a wall, or null when the wall has none.</summary>
    Task<WallTemperatureReading?> GetLatestReadingAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>
    /// Timestamp of the oldest reading of a wall, or null when it has none. Lets a caller offer an
    /// "all time" window without guessing how far back the series reaches.
    /// </summary>
    Task<DateTimeOffset?> GetEarliestReadingAtAsync(Guid wallId, CancellationToken ct = default);
}
