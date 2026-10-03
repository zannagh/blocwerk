// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Corrections;

/// <summary>
/// Runs the post-capture chain again for a capture whose model was corrected or reverted: every phase in order (place
/// the holds, refine their shapes, the photo-real steps, then the slow proposals and the coverage report), on the model
/// the capture now points at. The chain's own rules apply (only for the wall's active model, failures isolated).
/// </summary>
public sealed class CorrectionFollowUpWorker(
    CorrectionFollowUpQueue queue, CaptureFollowUpChain chain, ILogger<CorrectionFollowUpWorker> logger) : BackgroundService
{
    private static readonly CaptureFollowUpPhase[] Phases =
        [CaptureFollowUpPhase.Model, CaptureFollowUpPhase.Final, CaptureFollowUpPhase.AfterCompletion];

    /// <summary>Runs every phase of the chain for one capture. Public for tests.</summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(Guid captureId, CancellationToken ct)
    {
        foreach (var phase in Phases)
        {
            await chain.RunAsync(captureId, phase, ct);
        }

        logger.LogInformation("Capture {CaptureId}: follow-up chain re-run on its corrected model", captureId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
