// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>Measures the feed's horizon lag every 15 s for <see cref="FeedMetrics"/> (Postgres only; SQLite has no horizon).</summary>
public sealed class FeedLagCollector : BackgroundService
{
    // Committed rows at or above xmin are visible but not yet servable; uncommitted ones are invisible here and not lag yet.
    private const string LagSql =
        "SELECT GREATEST(COALESCE(EXTRACT(EPOCH FROM now() - MIN(\"CreatedAt\"))::float8, 0), 0) AS \"Value\" "
        + "FROM \"WallChanges\" WHERE \"TxId\" >= pg_snapshot_xmin(pg_current_snapshot())::text::bigint";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    // App-wide telemetry outside any session, so the session-less factory (see RootDbContextFactory).
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ILogger<FeedLagCollector> logger;

    public FeedLagCollector(RootDbContextFactory dbContextFactory, ILogger<FeedLagCollector> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.logger = logger;
    }

    /// <summary>The age in seconds of the oldest committed row the horizon still holds back, 0 when none (or on SQLite).</summary>
    public static async Task<double> MeasureAsync(BlocwerkDbContext db, CancellationToken cancellationToken) =>
        db.Database.IsNpgsql() ? await db.Database.SqlQueryRaw<double>(LagSql).SingleAsync(cancellationToken) : 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        FeedMetrics.Initialize();
        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            do
            {
                await CollectAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            FeedMetrics.PublishHorizonLag(await MeasureAsync(db, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A transient DB hiccup must not take the app down; the gauge keeps its last value.
            logger.LogWarning(ex, "Feed horizon lag measurement failed; the gauge keeps its last value.");
        }
    }
}
