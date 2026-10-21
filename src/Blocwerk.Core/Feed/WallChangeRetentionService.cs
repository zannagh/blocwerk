// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>Runs <see cref="WallChangeRetention"/> once a day, the first time a few minutes after startup.</summary>
public sealed class WallChangeRetentionService : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    // App-wide maintenance outside any session, so the session-less factory (see RootDbContextFactory).
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ILogger<WallChangeRetentionService> logger;

    public WallChangeRetentionService(RootDbContextFactory dbContextFactory, ILogger<WallChangeRetentionService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var deleted = await WallChangeRetention.RunAsync(db, DateTimeOffset.UtcNow - WallChangeRetention.Window, cancellationToken);
            if (deleted > 0)
            {
                logger.LogInformation("Feed retention deleted {Count} rows older than {Days} days.", deleted, WallChangeRetention.Window.TotalDays);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The rows only age a day longer; the next run deletes them.
            logger.LogWarning(ex, "Feed retention failed; it is retried tomorrow.");
        }
    }
}
