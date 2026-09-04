using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The single, low-priority consumer of the beta-video normalize queue. Drains
/// <see cref="BetaVideoEncodingStatus.Pending"/> clips one at a time (transcoding is CPU-heavy and the
/// prod box is one small container), turning each into a web-safe MP4 via <see cref="BetaVideoNormalizer"/>.
/// </summary>
/// <remarks>
/// The queue is the database, not an in-memory list, so a backlog survives restarts and a new upload
/// or an admin re-encode is picked up simply by the row being <c>Pending</c>. The
/// <see cref="BetaVideoNormalizationSignal"/> only wakes this loop early; without it the fallback poll
/// still finds the work. On boot any clip left <c>Processing</c> by a previous shutdown is reset to
/// <c>Pending</c> so it is retried rather than stranded.
/// </remarks>
public sealed class BetaVideoNormalizationService : BackgroundService
{
    /// <summary>Re-poll even without a signal, so nothing is ever stranded by a missed wake.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly RootDbContextFactory dbContextFactory;
    private readonly BetaVideoNormalizer normalizer;
    private readonly BetaVideoNormalizationSignal signal;
    private readonly ILogger<BetaVideoNormalizationService> logger;

    public BetaVideoNormalizationService(
        RootDbContextFactory dbContextFactory,
        BetaVideoNormalizer normalizer,
        BetaVideoNormalizationSignal signal,
        ILogger<BetaVideoNormalizationService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.normalizer = normalizer;
        this.signal = signal;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ResetStaleProcessingAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid? next;
            try
            {
                next = await NextPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A transient DB hiccup must not kill the worker; wait and retry.
                logger.LogWarning(ex, "Beta normalizer could not read the queue; retrying after the poll interval.");
                next = null;
            }

            if (next is { } videoId)
            {
                try
                {
                    await normalizer.ProcessAsync(videoId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // ProcessAsync is meant to contain its own failures, but a defence-in-depth catch
                    // here guarantees a single bad clip or DB blip can NEVER escape ExecuteAsync and
                    // trip BackgroundServiceExceptionBehavior.StopHost (which would take the whole app
                    // down, potentially crash-looping after a deploy). Log and move to the next clip.
                    logger.LogError(ex, "Beta normalizer failed to process clip {VideoId}; continuing.", videoId);
                }

                // Straight on to the next pending clip; only idle waits on the signal.
                continue;
            }

            try
            {
                await signal.WaitAsync(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<Guid?> NextPendingAsync(CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var id = await db.BetaVideos
            .Where(v => v.EncodingStatus == BetaVideoEncodingStatus.Pending)
            .OrderBy(v => v.CreatedAt)
            .Select(v => v.Id)
            .FirstOrDefaultAsync(ct);

        return id == Guid.Empty ? null : id;
    }

    private async Task ResetStaleProcessingAsync(CancellationToken ct)
    {
        try
        {
            await using var db = dbContextFactory.CreateDbContext();
            var reset = await db.BetaVideos
                .Where(v => v.EncodingStatus == BetaVideoEncodingStatus.Processing)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.EncodingStatus, BetaVideoEncodingStatus.Pending), ct);

            if (reset > 0)
            {
                logger.LogInformation("Reset {Count} beta clip(s) left Processing by a previous shutdown back to Pending.", reset);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reset stale Processing beta clips on startup.");
        }
    }
}
