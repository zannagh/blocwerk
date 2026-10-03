// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Corrections;

/// <summary>
/// Runs the post-capture chain again for a capture whose model was corrected or reverted: every phase in order (place
/// the holds, refine their shapes, the photo-real steps, then the slow proposals and the coverage report), on the model
/// the capture now points at. The chain's own rules apply (only for the wall's active model, failures isolated). The
/// work is marked on the capture's record (<see cref="CaptureFollowUpRecord.RunAgain"/>), so on start every capture a
/// previous process left marked is queued again.
/// </summary>
public sealed class CorrectionFollowUpWorker(
    CorrectionFollowUpQueue queue, CaptureFollowUpChain chain, RootDbContextFactory dbContextFactory, ILogger<CorrectionFollowUpWorker> logger)
    : BackgroundService
{
    /// <summary>Runs every phase of the chain for one marked capture. Public for tests.</summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(Guid captureId, CancellationToken ct)
    {
        await chain.RunAgainAsync(captureId, ct);
        logger.LogInformation("Capture {CaptureId}: follow-up chain re-run on its corrected model", captureId);
    }

    /// <summary>Queues every capture whose chain a previous process left to run again. Public for tests.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            await using var db = dbContextFactory.CreateDbContext();
            var marked = await db.WallCaptures
                .Where(c => c.FollowUpJson != null && c.FollowUpJson.Contains(CaptureFollowUpRecord.RunAgainMarker))
                .Select(c => c.Id)
                .ToListAsync(ct);
            foreach (var captureId in marked)
            {
                queue.Enqueue(captureId);
            }

            if (marked.Count > 0)
            {
                logger.LogInformation("Resuming the follow-up chain of {Count} corrected capture(s)", marked.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not recover the follow-up chains of corrected captures on startup");
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
                logger.LogWarning(ex, "Capture {CaptureId}: the follow-up chain after a correction could not run", captureId);
            }
        }
    }
}
