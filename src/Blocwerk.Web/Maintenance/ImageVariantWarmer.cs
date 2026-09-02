using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Maintenance;

/// <summary>
/// Pre-generates every allowed rendition of every servable image, into the same disk cache the
/// request path reads. Without it the first viewer of each image pays a full decode and rescale of
/// a camera original.
/// </summary>
/// <remarks>
/// Idempotent by construction: it calls <see cref="IImageVariantCache.GetOrCreateAsync"/>, which
/// answers a cached rendition from disk without ever invoking the loader — so a second run reads
/// four small files per image and touches neither Postgres nor the file store. It is also safely
/// interruptible: the cache commits each file with an atomic rename, so stopping mid-run leaves a
/// partially warm cache and nothing else. Enumeration happens up front and closes its connections
/// before any rendering starts; each miss then opens its own short-lived context.
/// </remarks>
public sealed class ImageVariantWarmer
{
    /// <summary>
    /// How many images are rendered at once. Deliberately small: this runs on the live single
    /// container, and one rendition of a 50 MP wall photo is a full decode plus rescale, which is
    /// CPU- and memory-hungry. Two keeps a core free for requests while still overlapping the
    /// database read of one image with the rendering of another.
    /// </summary>
    public const int Concurrency = 2;

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly IWallImageStorage storage;
    private readonly IImageVariantCache variants;
    private readonly ILogger<ImageVariantWarmer> logger;

    public ImageVariantWarmer(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IWallImageStorage storage,
        IImageVariantCache variants,
        ILogger<ImageVariantWarmer> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.storage = storage;
        this.variants = variants;
        this.logger = logger;
    }

    /// <summary>Warms every rendition of every servable image, reporting as it goes.</summary>
    public async Task<ImageWarmSummary> WarmAsync(MaintenanceJobLog log, CancellationToken ct)
    {
        var startedAt = TimeProvider.System.GetTimestamp();

        log.Report("Enumerating images...");
        var targets = await ImageWarmTargets.CollectAsync(dbContextFactory, storage, ct);
        log.Append($"{targets.Count} images to warm across {ImageVariants.Widths.Length} widths.");

        var counters = new Counters();
        var done = 0;

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct },
            async (target, token) =>
            {
                await WarmOneAsync(target, counters, log, token);
                log.Report($"{Interlocked.Increment(ref done)} / {targets.Count} images...");
            });

        var summary = new ImageWarmSummary(
            targets.Count,
            counters.Generated,
            counters.Skipped,
            counters.Failed,
            counters.BytesWritten,
            TimeProvider.System.GetElapsedTime(startedAt));

        logger.LogInformation("Image variant warming finished: {Summary}", summary);
        return summary;
    }

    /// <summary>
    /// Warms one image at every width. The original is fetched at most once and reused across the
    /// widths, so a cold image costs one blob read rather than four. Widths ascend and the loop
    /// stops at the first that reports the source is already narrower, since every wider rendition
    /// would report the same.
    /// </summary>
    private async Task WarmOneAsync(
        ImageWarmTarget target, Counters counters, MaintenanceJobLog log, CancellationToken ct)
    {
        var original = new CachedOriginal(target.Load);

        try
        {
            foreach (var width in ImageVariants.Widths)
            {
                var callsBefore = original.Calls;
                var variant = await variants.GetOrCreateAsync(target.Key, width, () => original.GetAsync(ct), ct);

                if (variant is null)
                {
                    counters.Fail();
                    log.Append($"FAILED {target.Description}: original could not be read.");
                    return;
                }

                if (variant.IsOriginal)
                {
                    counters.Skip(ImageVariants.Widths.Length - Array.IndexOf(ImageVariants.Widths, width));
                    return;
                }

                // The loader being reached is exactly what distinguishes a rendition this run
                // produced from one an earlier run left on disk: the cache answers a hit off the
                // file without ever asking for the original.
                if (original.Calls > callsBefore)
                {
                    counters.Generate(variant.Bytes.LongLength);
                }
                else
                {
                    counters.Skip(1);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One unreadable image must not end the run.
            counters.Fail();
            logger.LogWarning(ex, "Could not warm variants for {Image}", target.Description);
            log.Append($"FAILED {target.Description}: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches the stored original at most once, however many widths ask for it, and counts how
    /// often it was asked — which is how the run tells a generated rendition from a cached one.
    /// One instance per image, used by one thread.
    /// </summary>
    private sealed class CachedOriginal(Func<CancellationToken, Task<byte[]?>> load)
    {
        private byte[]? bytes;
        private bool fetched;

        /// <summary>How many widths reached the loader, i.e. missed the cache.</summary>
        public int Calls { get; private set; }

        public async Task<byte[]?> GetAsync(CancellationToken ct)
        {
            Calls++;

            if (!fetched)
            {
                bytes = await load(ct);
                fetched = true;
            }

            return bytes;
        }
    }

    private sealed class Counters
    {
        private int generated;
        private int skipped;
        private int failed;
        private long bytes;

        public int Generated => Volatile.Read(ref generated);

        public int Skipped => Volatile.Read(ref skipped);

        public int Failed => Volatile.Read(ref failed);

        public long BytesWritten => Interlocked.Read(ref bytes);

        public void Generate(long written)
        {
            Interlocked.Increment(ref generated);
            Interlocked.Add(ref bytes, written);
        }

        public void Skip(int count) => Interlocked.Add(ref skipped, count);

        public void Fail() => Interlocked.Increment(ref failed);
    }
}
