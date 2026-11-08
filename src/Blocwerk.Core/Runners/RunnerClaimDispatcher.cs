// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Recognition;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The claim long-poll over both queues (runner protocol 1.2): a job the runner still holds first (re-attach after a
/// restart), then a recognition job (only for a runner that advertises a recognition kind), then a GPU job. The instance
/// runner never gets a GPU job, nor does any runner with <see cref="GpuRunnerMode.Off"/>. A runner holds at most one job of
/// either queue at a time.
/// </summary>
public sealed class RunnerClaimDispatcher(GpuJobQueue gpuQueue, RecognitionQueue recognitionQueue, GpuJobSignal signal)
{
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(3);

    /// <summary>Long-poll: a claimed (or re-attached) job of either queue, or null after <paramref name="wait"/>.</summary>
    public async Task<RunnerClaim?> ClaimAsync(GpuRunner runner, TimeSpan wait, RunnerClaimRequest? request, CancellationToken ct)
    {
        var clock = gpuQueue.Clock;
        var deadline = clock.GetUtcNow() + wait;
        if (runner.Paused is true)
        {
            await gpuQueue.ClearPausedAsync(runner.Id, ct);
        }

        while (true)
        {
            if (await ClaimOnceAsync(runner, request, ct) is { } claim)
            {
                return claim;
            }

            var left = deadline - clock.GetUtcNow();
            if (left <= TimeSpan.Zero)
            {
                return null;
            }

            await signal.WaitAsync(left < RecheckInterval ? left : RecheckInterval, ct);
        }
    }

    /// <summary>One attempt over both queues.</summary>
    public async Task<RunnerClaim?> ClaimOnceAsync(GpuRunner runner, RunnerClaimRequest? request, CancellationToken ct)
    {
        var gpu = runner.Scope == RunnerScope.Owned && gpuQueue.Options.Mode != GpuRunnerMode.Off;
        var holdsRecognition = RunnerCapabilities.HasRecognition(runner.Capabilities) && await recognitionQueue.HoldsClaimAsync(runner.Id, ct);
        if (!holdsRecognition && gpu && await gpuQueue.HoldsClaimAsync(runner.Id, ct))
        {
            return await gpuQueue.ClaimOnceAsync(runner, request, ct);
        }

        var (job, reattached) = await recognitionQueue.ClaimOrReattachAsync(runner, ct);
        if (job is not null)
        {
            return new RunnerClaim(
                job.Id, "draft", job.LeaseSeconds, job.InputBytes, job.InputSha256, Previews: false, reattached,
                job.WallId, CaptureId: null, job.Kind, job.RequiredMemoryMb);
        }

        return holdsRecognition || !gpu ? null : await gpuQueue.ClaimOnceAsync(runner, request, ct);
    }
}
