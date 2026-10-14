// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Runs the slow after-completion steps of completed captures (the hold proposal search, the coverage report), a few
/// captures at a time (<see cref="Parallel"/>), BESIDE the capture worker: a capture that just completed no longer holds the next one (an update's 3D step, a
/// retrain hand-back) back for the half hour a search can take. The chain's own rules apply (only for the wall's active
/// model and a completed capture, failures isolated). On start every capture a previous process left marked is queued again.
/// </summary>
public sealed class AfterCompletionWorker(
    AfterCompletionQueue queue, CaptureFollowUpChain chain, RootDbContextFactory dbContextFactory, ILogger<AfterCompletionWorker> logger)
    : BackgroundService
{
    /// <summary>
    /// How many captures run their after-completion steps at once. A runner's hold search can wait for hours, so one slot
    /// would hold every other capture back; the in-app detection itself stays one at a time (the hold search's own gate), so
    /// the shared box never runs two heavy in-app searches together.
    /// </summary>
    public const int Parallel = 3;

    private readonly SemaphoreSlim slots = new(Parallel, Parallel);
    private readonly ConcurrentDictionary<Guid, byte> inFlight = new();

    /// <summary>Tests: what a slot runs instead of the chain (the scheduling is what they look at).</summary>
    internal Func<Guid, CancellationToken, Task<bool>>? RunOverride { get; init; }

    /// <summary>Runs the after-completion steps of one marked capture. Public for tests.</summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether a newer mark (the capture completed again meanwhile) is left to run.</returns>
    public Task<bool> RunAsync(Guid captureId, CancellationToken ct) => chain.RunAfterCompletionAsync(captureId, ct);

    /// <summary>Queues every capture whose after-completion steps a previous process left to run. Public for tests.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            await using var db = dbContextFactory.CreateDbContext();
            var marked = await db.WallCaptures
                .Where(c => c.FollowUpJson != null && c.FollowUpJson.Contains(CaptureFollowUpRecord.AfterCompletionMarker))
                .OrderBy(c => c.CompletedAt)
                .Select(c => c.Id)
                .ToListAsync(ct);
            var resumed = 0;
            foreach (var captureId in marked)
            {
                // At most MaxRecoveries starts in a row: steps that bring the process down must not loop it.
                if (await chain.MayRecoverAsync(captureId, CaptureFollowUpRecoveryKind.AfterCompletion, ct))
                {
                    queue.Enqueue(captureId);
                    resumed++;
                }
            }

            if (resumed > 0)
            {
                logger.LogInformation("Resuming the after-completion steps of {Count} capture(s)", resumed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not recover the after-completion steps of captures on startup");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        var running = new List<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var captureId = await queue.DequeueAsync(stoppingToken);
                await slots.WaitAsync(stoppingToken);
                running.RemoveAll(t => t.IsCompleted);
                running.Add(RunInSlotAsync(captureId, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: the runs in flight stop at their own cancellation; their marks stay for the next start.
        }

        await Task.WhenAll(running);
    }

    /// <summary>
    /// One capture's run in one of the <see cref="Parallel"/> slots (never the same capture twice at once: a second hand-off
    /// while it runs is dropped, its mark is still read by the run in flight or the next start).
    /// </summary>
    private async Task RunInSlotAsync(Guid captureId, CancellationToken ct)
    {
        if (!inFlight.TryAdd(captureId, 0))
        {
            slots.Release();
            return;
        }

        try
        {
            await Task.Yield();
            if (await (RunOverride ?? chain.RunAfterCompletionAsync)(captureId, ct))
            {
                // Completed again while this run worked: its newer mark gets its own run.
                queue.Enqueue(captureId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Capture {CaptureId}: the after-completion steps could not run", captureId);
        }
        finally
        {
            inFlight.TryRemove(captureId, out _);
            slots.Release();
        }
    }
}
