namespace Blocwerk.Web.Maintenance;

/// <summary>What a warming run did.</summary>
/// <param name="Images">Images visited.</param>
/// <param name="Generated">Renditions rendered and written to the cache.</param>
/// <param name="Skipped">Renditions already cached, or not worth caching because the stored
/// original is already narrower than the requested width.</param>
/// <param name="Failed">Images whose original could not be read or decoded.</param>
/// <param name="BytesWritten">Total size of the renditions written.</param>
/// <param name="Elapsed">Wall-clock duration.</param>
public sealed record ImageWarmSummary(
    int Images,
    int Generated,
    int Skipped,
    int Failed,
    long BytesWritten,
    TimeSpan Elapsed)
{
    public override string ToString() =>
        $"{Images} images visited, {Generated} variants generated ({Bytes(BytesWritten)}), " +
        $"{Skipped} skipped, {Failed} failed, in {Elapsed.TotalSeconds:F1}s.";

    private static string Bytes(long value) => value switch
    {
        >= 1024L * 1024 => $"{value / 1024d / 1024d:F1} MB",
        >= 1024 => $"{value / 1024d:F0} kB",
        _ => $"{value} B",
    };
}
