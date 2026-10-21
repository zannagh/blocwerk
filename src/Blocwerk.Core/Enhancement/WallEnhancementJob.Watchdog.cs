// <copyright file="WallEnhancementJob.Watchdog.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// The step watchdog: a step past <see cref="StepTimeout"/> is cancelled, and the next step starts only once the
/// timed-out work has really returned. Work that ignores the cancellation for <see cref="StopGrace"/> stops the run
/// there; the job then waits for that work (staying live, so Discard, Finalize and wall updates keep waiting too).
/// </summary>
public sealed partial class WallEnhancementJob
{
    private async Task<(WallEnhancementStepOutcome? Outcome, WallEnhancementStepOutcomeKind Kind, string? Stale)> RunWatchedAsync(
        WallEnhancementRun run, IWallEnhancementStep step, WallEnhancementStepTracker tracker, CancellationToken ct)
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(StepTimeout);
        var work = RunInBatchAsync(run, step, tracker, watchdog.Token);
        await Task.WhenAny(work, UntilCancelledAsync(watchdog.Token));
        if (!work.IsCompleted && await Task.WhenAny(work, Task.Delay(StopGrace, CancellationToken.None)) != work)
        {
            if (ct.IsCancellationRequested && !cancelled.ContainsKey(run.Id))
            {
                // A shutdown, not a cancel: the run stays Running and resumes on the next start (the step runs again).
                ct.ThrowIfCancellationRequested();
            }

            var why = ct.IsCancellationRequested ? "was cancelled" : $"ran past its {StepTimeout.TotalHours:0.#} h limit";
            logger.LogWarning("Wall enhancement {RunId}: step {Step} {Why} and did not stop; the run stops there", run.Id, step.Key, why);
            var stopped = $"{step.Title} {why} and did not stop. The run stopped there; it can be retried or discarded once that work has ended.";
            if (cancelled.ContainsKey(run.Id))
            {
                // An admin's cancel: the review opens (every unfinished step failed) as for a step that did stop.
                await MarkCancelledAsync(run.Id);
            }
            else
            {
                await MarkFailedAsync(run.Id, stopped);
            }

            await ObserveAsync(run.Id, work);
            ct.ThrowIfCancellationRequested();
            return (null, WallEnhancementStepOutcomeKind.Failed, stopped);
        }

        try
        {
            return await work;
        }
        catch (OperationCanceledException) when (watchdog.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Wall enhancement {RunId}: step {Step} ran longer than {Timeout} and was stopped", run.Id, step.Key, StepTimeout);
            return (WallEnhancementStepOutcome.Done($"{step.Title} took longer than {StepTimeout.TotalHours:0.#} h and was stopped."),
                WallEnhancementStepOutcomeKind.Failed, null);
        }
    }

    private static async Task UntilCancelledAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException)
        {
            // The watchdog fired (or the run was cancelled).
        }
    }

    /// <summary>
    /// Waits for work that was left running, whatever it ends with, but at most <see cref="ObserveLimit"/>: work that never
    /// returns would otherwise keep the run live (and the wall's updates, Discard and Retry waiting) until a restart.
    /// After the limit the job lets go; the work is abandoned to whatever it does next, and that is logged as an error.
    /// </summary>
    private async Task ObserveAsync(Guid runId, Task work)
    {
        try
        {
            if (await Task.WhenAny(work, Task.Delay(ObserveLimit, CancellationToken.None)) != work)
            {
                logger.LogError(
                    "Wall enhancement {RunId}: a stopped step still has not returned after {Limit}; the run is released and the step's work abandoned",
                    runId, ObserveLimit);
                return;
            }

            await work;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "A stopped wall enhancement step ended late");
        }
    }
}
