// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Recognition;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The claim long-poll over both queues (runner protocol 1.2): a job the runner still holds first (re-attach after a
/// restart), then a recognition job (only for a runner that advertises a recognition kind), then a GPU job. The instance
/// runner never gets a GPU job, nor does any runner with <see cref="GpuRunnerMode.Off"/>. A runner holds at most one job of
/// either queue at a time: while it holds one in either, it claims nothing new in the other, and its claims are serialized
/// per runner so two processes on one key cannot each take a job from a different queue.
/// </summary>
/// <remarks>
/// TODO: the per-runner gate is per process. Replicas of the web app sharing the database could still hand one key a job
/// from each queue at the same moment; one claim per runner across replicas needs a database-side guard.
/// </remarks>
public sealed class RunnerClaimDispatcher(GpuJobQueue gpuQueue, RecognitionQueue recognitionQueue, GpuJobSignal signal)
{
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> runnerGates = new();

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

    /// <summary>One attempt over both queues, serialized per runner.</summary>
    public async Task<RunnerClaim?> ClaimOnceAsync(GpuRunner runner, RunnerClaimRequest? request, CancellationToken ct)
    {
        var gate = runnerGates.GetOrAdd(runner.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await ClaimForRunnerAsync(runner, request, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RunnerClaim?> ClaimForRunnerAsync(GpuRunner runner, RunnerClaimRequest? request, CancellationToken ct)
    {
        var gpu = runner.Scope == RunnerScope.Owned && gpuQueue.Options.Mode != GpuRunnerMode.Off;

        // A held GPU job only comes back (re-attach) while GPU work is on; with runners off the runner waits it out.
        if (runner.Scope == RunnerScope.Owned && await gpuQueue.HoldsClaimAsync(runner.Id, ct))
        {
            return gpu ? await gpuQueue.ClaimOnceAsync(runner, request, ct) : null;
        }

        // Re-attaches a held recognition job, or claims a new one when the runner holds none.
        var (job, reattached) = await recognitionQueue.ClaimOrReattachAsync(runner, ct);
        if (job is not null)
        {
            return new RunnerClaim(
                job.Id, "draft", job.LeaseSeconds, job.InputBytes, job.InputSha256, Previews: false, reattached,
                job.WallId, CaptureId: null, job.Kind, job.RequiredMemoryMb, job.RunSeconds);
        }

        return !gpu || await recognitionQueue.HoldsClaimAsync(runner.Id, ct) ? null : await gpuQueue.ClaimOnceAsync(runner, request, ct);
    }
}
