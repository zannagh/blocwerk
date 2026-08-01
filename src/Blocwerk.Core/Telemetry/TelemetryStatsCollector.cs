using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Telemetry;

/// <summary>
/// Feeds the "how many exist right now" observable gauges (total walls, boulders, users, holds,
/// active sessions). These are counts, not events, so polling the database on an interval is far
/// cheaper than tracking every mutation — and it self-heals if a write path forgets to increment.
/// <para>
/// The gauges are read on each metric export (~every 60s by default), so a 30s poll keeps them
/// fresh without adding meaningful load. A fresh <see cref="BlocwerkDbContext"/> with the default
/// empty <c>CurrentUserId</c> bypasses the per-user wall query filter, so the counts are global.
/// </para>
/// </summary>
public sealed class TelemetryStatsCollector : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ILogger<TelemetryStatsCollector> logger;

    public TelemetryStatsCollector(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ILogger<TelemetryStatsCollector> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The gauges register with the meter as soon as the type is touched; make sure that
        // happens even if no request has recorded a counter yet.
        BlocwerkMetrics.Initialize();

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            await CollectAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            db.CurrentUserId = Guid.Empty;

            var walls = await db.Walls.CountAsync(cancellationToken);
            var boulders = await db.Boulders.CountAsync(b => !b.IsArchived, cancellationToken);
            var users = await db.Users.CountAsync(cancellationToken);
            var holds = await db.Holds.CountAsync(cancellationToken);
            var sessions = await db.ClimbingSessions.CountAsync(s => s.EndedAt == null, cancellationToken);

            BlocwerkMetrics.UpdateStats(walls, boulders, users, holds, sessions);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // A transient DB hiccup must not take the app down; the gauges just hold their
            // previous values until the next tick.
            logger.LogWarning(ex, "Telemetry stats collection failed; gauges keep their last values.");
        }
    }
}
