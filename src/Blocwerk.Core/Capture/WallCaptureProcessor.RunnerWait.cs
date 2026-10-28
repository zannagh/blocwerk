// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// A markerless capture whose reconstruction waits for a 3D runner (<see cref="Runners.GpuRunnerOptions.SplatOnRunners"/>): the
/// capture worker does not wait. The capture is parked in <see cref="WallCaptureStatus.Solving"/> (still running, so the wall takes
/// no other capture) with its job id stored; the job queue wakes it when a runner delivers or the job ends (after
/// <see cref="Runners.GpuRunnerOptions.MarkerlessQueuedLifetime"/> unclaimed it fails, the photos stay). A restart finds the
/// same job and parks again.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    internal const string ReconstructWaitStage = "Waiting for a 3D runner to reconstruct the wall; it will be done once one becomes available";

    /// <summary>True when the model was imported; false when the capture was parked to wait for a runner.</summary>
    private async Task<bool> SfmAndImportOrParkAsync(CaptureRun run, IComputeJobClient geometry, CancellationToken ct)
    {
        try
        {
            await SfmAndImportAsync(run, geometry, ct);
            return true;
        }
        catch (ComputeJobException ex) when (ex.Kind == ComputeFailureKind.Deferred)
        {
            logger.LogInformation("Capture {CaptureId}: its reconstruction waits for a 3D runner", run.Capture.Id);
            await UpdateAsync(
                run.Capture.Id,
                c =>
                {
                    // Waiting is no restart: the wake-ups must not eat the capture's attempt budget.
                    c.Status = WallCaptureStatus.Solving;
                    c.Stage = ReconstructWaitStage;
                    c.Progress = 0.2;
                    c.Attempts = 0;
                },
                ct);
            return false;
        }
    }
}
