// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Runs the slow after-completion steps of completed captures (the hold proposal search, the coverage report) one capture at
/// a time, BESIDE the capture worker: a capture that just completed no longer holds the next one (an update's 3D step, a
/// retrain hand-back) back for the half hour a search can take. The chain's own rules apply (only for the wall's active
/// model and a completed capture, failures isolated). On start every capture a previous process left marked is queued again.
/// </summary>
public sealed class AfterCompletionWorker(
    AfterCompletionQueue queue, CaptureFollowUpChain chain, RootDbContextFactory dbContextFactory, ILogger<AfterCompletionWorker> logger)
    : BackgroundService
{
    /// <summary>Runs the after-completion steps of one marked capture. Public for tests.</summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task RunAsync(Guid captureId, CancellationToken ct) => chain.RunAfterCompletionAsync(captureId, ct);

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
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid captureId;
            try
            {
                captureId = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RunAsync(captureId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Capture {CaptureId}: the after-completion steps could not run", captureId);
            }
        }
    }
}
