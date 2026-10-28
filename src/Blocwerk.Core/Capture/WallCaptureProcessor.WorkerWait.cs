// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// A 3D runner delivered the trained view but the splat worker (which finishes it) is down, or the finish itself waits for a
/// 3D runner (<see cref="Runners.GpuRunnerOptions.SplatOnRunners"/>): the capture shows one stable "waiting" state while the
/// job queue's sweep retries (or the runner delivers), instead of completing and being reopened on every retry (the final
/// follow-up runs once, when the view is stored). After the give-up time it says why there is no view.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    internal const string WaitingForWorkerStage = "Photo-real view: waiting for the 3D worker";

    internal const string WaitingForRunnerStage = "Photo-real view: waiting for a 3D runner to finish it";

    /// <summary>
    /// True when the capture has a delivered, unfinished runner result and was parked (or ended after the give-up time);
    /// false when there is no such result and the capture ends without the view as it always did. <paramref name="onRunner"/>:
    /// the finish waits for a runner, not for the worker.
    /// </summary>
    private async Task<bool> WaitForFinishWorkerAsync(CaptureRun run, CancellationToken ct, bool onRunner = false)
    {
        var captureId = run.Capture.Id;
        var job = gpuJobs is null ? null : await gpuJobs.LatestForCaptureAsync(captureId, ct);
        if (gpuJobs is null || job is not { Status: GpuJobStatus.Succeeded, InstalledAt: null })
        {
            return false;
        }

        // A finish waiting for a runner is never given up here: its own queue lifetime ends it (and wakes the capture).
        if (onRunner || !gpuJobs.FinishGivenUp(job))
        {
            logger.LogInformation(
                "Capture {CaptureId}: its delivered photo-real view waits for {What}", captureId, onRunner ? "a 3D runner to finish it" : "the 3D worker (down)");
            await UpdateAsync(
                captureId,
                c =>
                {
                    // Not a restart: the retries must not eat the capture's attempt budget.
                    c.Status = WallCaptureStatus.Splatting;
                    c.Stage = onRunner ? WaitingForRunnerStage : WaitingForWorkerStage;
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
