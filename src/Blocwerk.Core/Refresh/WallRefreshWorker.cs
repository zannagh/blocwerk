// <copyright file="WallRefreshWorker.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// Moves "Update panels + 3D" runs forward: re-enqueues unfinished runs on start, processes up to
/// <see cref="MaxParallel"/> runs at a time (one wall's steps never overlap, see <see cref="WallRefreshProcessor"/>,
/// so a long video join on one wall does not hold up another), and discards runs left idle (hourly).
/// </summary>
public sealed class WallRefreshWorker(
    RootDbContextFactory dbContextFactory,
    WallRefreshQueue queue,
    WallRefreshProcessor processor,
    ILogger<WallRefreshWorker> logger) : BackgroundService
{
    private const int MaxParallel = 3;
    private static readonly TimeSpan SweepEvery = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        var sweeping = SweepLoopAsync(stoppingToken);
        using var slots = new SemaphoreSlim(MaxParallel, MaxParallel);
        var running = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid refreshId;
            try
            {
                await slots.WaitAsync(stoppingToken);
                refreshId = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            running.RemoveAll(t => t.IsCompleted);
            running.Add(RunOneAsync(refreshId, slots, stoppingToken));
        }

        await Task.WhenAll(running.Append(sweeping));
    }

    private async Task RunOneAsync(Guid refreshId, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            await Task.Yield();
            await processor.ProcessAsync(refreshId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down; the row status resumes it on the next start.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Wall refresh worker failed on {RefreshId}; continuing", refreshId);
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SweepEvery);
        try
        {
            do
            {
                try
                {
                    await processor.DiscardStaleAsync(DateTimeOffset.UtcNow, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not discard idle wall refreshes");
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            await using var db = dbContextFactory.CreateDbContext();
            var unfinished = await db.WallRefreshes
                .Where(r => r.Status == WallRefreshStatus.Sorting || r.Status == WallRefreshStatus.Running
                            || r.Status == WallRefreshStatus.Applying
                            || (r.Status == WallRefreshStatus.ReadyToApply && r.SummaryRequestedAt != null))
                .Select(r => r.Id)
                .ToListAsync(ct);
            foreach (var id in unfinished)
            {
                queue.Enqueue(id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not recover unfinished wall refreshes on startup");
        }
    }
}
