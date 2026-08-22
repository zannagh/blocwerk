namespace Blocwerk.Core.Services;

/// <summary>
/// Raw shape returned by the bucket aggregate: the bucket index counted from the start of the
/// requested window, plus the aggregates of the rows that fell into it. The index is turned into
/// a timestamp on the client, so the database never has to build one.
/// </summary>
internal sealed record TemperatureBucketRow(
    long Bucket,
    double Average,
    double Minimum,
    double Maximum,
    int Samples);
