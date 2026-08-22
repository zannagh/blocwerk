namespace Blocwerk.Core.Services;

/// <summary>
/// One aggregated slice of a wall's temperature series, computed by the database rather than by
/// reading the raw samples. <paramref name="BucketStart"/> is the inclusive start of the slice;
/// slices without a single sample are absent from a result rather than being reported as empty.
/// </summary>
public sealed record WallTemperatureBucket(
    DateTimeOffset BucketStart,
    double AverageCelsius,
    double MinCelsius,
    double MaxCelsius,
    int SampleCount);
