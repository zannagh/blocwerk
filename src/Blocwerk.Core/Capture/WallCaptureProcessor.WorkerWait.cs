// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// A 3D runner delivered the trained view but the splat worker (which finishes it) is down: the capture shows one stable
/// "waiting for the 3D worker" state while the job queue's sweep retries, instead of completing and being reopened on every
/// retry (the final follow-up runs once, when the view is stored). After the give-up time it says why there is no view.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    internal const string WaitingForWorkerStage = "Photo-real view: waiting for the 3D worker";

    /// <summary>
    /// True when the capture has a delivered, unfinished runner result and was parked (or ended after the give-up time);
    /// false when there is no such result and the capture ends without the view as it always did.
    /// </summary>
    private async Task<bool> WaitForFinishWorkerAsync(CaptureRun run, CancellationToken ct)
    {
        var captureId = run.Capture.Id;
        var job = gpuJobs is null ? null : await gpuJobs.LatestForCaptureAsync(captureId, ct);
        if (gpuJobs is null || job is not { Status: GpuJobStatus.Succeeded, InstalledAt: null })
        {
            return false;
        }

        if (!gpuJobs.FinishGivenUp(job))
        {
            logger.LogInformation("Capture {CaptureId}: the 3D worker is down; its delivered photo-real view waits for it", captureId);
            await UpdateAsync(
                captureId,
                c =>
                {
                    // Not a restart: the retries must not eat the capture's attempt budget.
                    c.Status = WallCaptureStatus.Splatting;
                    c.Stage = WaitingForWorkerStage;
                    c.Progress = 0.96;
                    c.Attempts = 0;
                },
                ct);
            return true;
        }

        const string reason = "the trained view could not be finished on the server (the 3D worker was unreachable for a day)";
        logger.LogWarning("Capture {CaptureId}: giving up on finishing its photo-real view: {Reason}", captureId, reason);
        if (await RestoreAfterRefinishAsync(captureId, reason, ct))
        {
            return true;
        }

        await gpuJobs.CloseAsync(job.Id, reason, ct);
        await FollowUpAsync(run, CaptureFollowUpPhase.Final, ct);
        await EndWithoutViewAsync(captureId, reason, ct);
        return true;
    }
}
