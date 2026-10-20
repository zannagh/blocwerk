// <copyright file="WallEnhancementJob.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// Runs a wall's one-time enhancement: every registered <see cref="IWallEnhancementStep"/> in order, each in its own
/// wall-scoped journal batch, each recorded on the run as it starts and as it ends. A recorded step never runs again, so
/// a run interrupted by a restart resumes with exactly the step that was cut off (all steps are idempotent, and the long
/// hold search checkpoints per photo). A step that throws is recorded as failed and the next one still runs; the run
/// then waits for its review, where a failed step can be retried.
/// </summary>
/// <remarks>
/// Before each step the job waits while someone has the wall's hold editor open (<see cref="IWallEditPresence"/>) and
/// while another writer holds the wall's hold lock, and AFTER both waits it checks the generation and that no wall
/// update opened: a run never writes to a generation it did not start on. During the step it holds the deploy gate and
/// reports an expected end for long steps (<see cref="IDeployBusyEta"/>).
/// </remarks>
public sealed partial class WallEnhancementJob(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    IWallEnhancementActorFactory actorFactory,
    IEnumerable<IWallEnhancementStep> steps,
    IChangeJournal journal,
    ILogger<WallEnhancementJob> logger,
    IDeployBusyGate? busyGate = null,
    IWallEditPresence? editPresence = null,
    IDeployBusyEta? busyEta = null)
{
    /// <summary>How long the job waits for another hold writer of the wall before the step fails.</summary>
    public static readonly TimeSpan WriterWait = TimeSpan.FromMinutes(10);

    private readonly IReadOnlyList<IWallEnhancementStep> ordered = steps.OrderBy(s => s.Order).ToList();
    private readonly ConcurrentDictionary<Guid, WallEnhancementStepTracker> trackers = new();

    /// <summary>The watchdog: a step that runs longer is stopped and recorded as failed (then retried or discarded).</summary>
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromHours(4);

    /// <summary>How often the run's heartbeat is renewed (<see cref="WallEnhancementGuard.StaleAfter"/> is far longer).</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How often the job looks again while an editor has the wall open.</summary>
    public TimeSpan EditorPoll { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The steps in the order they run.</summary>
    public IReadOnlyList<IWallEnhancementStep> Steps => ordered;

    /// <summary>How far the running step of the run is, when it reports progress.</summary>
    /// <param name="runId">The run.</param>
    /// <returns>The progress and the expected end, or null.</returns>
    public (WallEnhancementProgress Progress, DateTimeOffset? ExpectedEnd)? ProgressOf(Guid runId) =>
        trackers.TryGetValue(runId, out var t) && t.Last is { } last ? (last, t.ExpectedEnd) : null;

    /// <summary>Runs (or resumes) the run. Never throws but on shutdown.</summary>
    /// <param name="runId">The run; it must be <see cref="WallEnhancementStatus.Running"/>.</param>
    /// <param name="shutdown">App shutdown: the run stays Running and resumes on the next start.</param>
    /// <param name="cancel">An admin's "Cancel this run": the step in hand is recorded as failed and the review opens.</param>
    /// <returns>When it stopped.</returns>
    public async Task RunAsync(Guid runId, CancellationToken shutdown, CancellationToken cancel = default)
    {
        using var either = CancellationTokenSource.CreateLinkedTokenSource(shutdown, cancel);
        var heartbeat = HeartbeatAsync(runId, either.Token);
        try
        {
            await RunCoreAsync(runId, either.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Shutdown: the run stays Running and the resumer continues it.
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            await MarkCancelledAsync(runId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Wall enhancement {RunId} failed", runId);
            await MarkFailedAsync(runId, ex.Message);
        }
        finally
        {
            await either.CancelAsync();
            await heartbeat;
            if (trackers.TryRemove(runId, out var tracker))
            {
                tracker.Clear();
            }
        }
    }

    private async Task RunCoreAsync(Guid runId, CancellationToken ct)
    {
        var run = await LoadAsync(runId, ct);
        if (run is not { Status: WallEnhancementStatus.Running })
        {
            return;
        }

        foreach (var step in ordered)
        {
            var recorded = WallEnhancementStepRecord.Parse((await LoadAsync(runId, ct))?.StepsJson).FirstOrDefault(r => r.Key == step.Key);
            if (recorded?.IsFinished == true)
            {
                continue;
            }

            await WaitForEditorsAsync(run.WallId, ct);
            if (!await RunOneAsync(run, step, recorded, ct))
            {
                return;
            }
        }

        await FinishAsync(runId, ct);
    }

    /// <summary>Runs one step and records it. False when the run had to stop (the wall moved on): it is marked failed.</summary>
    private async Task<bool> RunOneAsync(WallEnhancementRun run, IWallEnhancementStep step, WallEnhancementStepRecord? recorded, CancellationToken ct)
    {
        if (await StaleReasonAsync(run, ct) is { } before)
        {
            await MarkFailedAsync(run.Id, before);
            return false;
        }

        var started = recorded?.StartedAt ?? DateTimeOffset.UtcNow;
        var running = new WallEnhancementStepRecord(
            step.Key, step.Title, WallEnhancementStepOutcomeKind.Running, string.Empty, started, null, recorded?.BatchIds ?? []);
        await SaveStepAsync(run.Id, running, step.Key, ct);
        var (outcome, kind, stale) = await RunStepAsync(run, step, ct);
        if (stale is not null)
        {
            await MarkFailedAsync(run.Id, stale);
            return false;
        }

        List<Guid> batches;
        await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            batches = await WallEnhancementJournal.StepBatchesAsync(db, run.WallId, run.CreatedByUserId, step, started, ct);
            await AddItemsAsync(db, run.Id, step, batches, outcome!.Suggestions, ct);
        }

        var done = running with { Outcome = kind, Summary = outcome.Summary, FinishedAt = DateTimeOffset.UtcNow, BatchIds = batches };
        await SaveStepAsync(run.Id, done, null, ct);
        logger.LogInformation(
            "Wall enhancement {RunId} on wall {WallId}, step {Step}: {Outcome} — {Summary} ({Batches} journal batches)",
            run.Id, run.WallId, step.Key, kind, outcome.Summary, batches.Count);
        return true;
    }

    private async Task<(WallEnhancementStepOutcome? Outcome, WallEnhancementStepOutcomeKind Kind, string? Stale)> RunStepAsync(
        WallEnhancementRun run, IWallEnhancementStep step, CancellationToken ct)
    {
        using var busy = busyGate?.Hold(DeployBusyWork.WallEnhancement, run.WallId, run.CreatedByUserId);
        var tracker = trackers[run.Id] = new WallEnhancementStepTracker(run.Id, busyEta);
        try
        {
            if (step.NeedsModel && !await HasActiveModelAsync(run.WallId, ct))
            {
                return (WallEnhancementStepOutcome.Skip("This wall has no 3D model yet."), WallEnhancementStepOutcomeKind.Skipped, null);
            }

            // Another writer of the wall's holds (a clean-up, a merge, a wall-update commit) finishes first: the steps
            // themselves fail fast on the lock, which in the background would only mean a failed step. That wait may have
            // let a wall update through, so the generation is checked again right before the step writes.
            (await WallHoldWriteLock.AcquireAsync(run.WallId, WriterWait, ct)).Dispose();
            if (await StaleReasonAsync(run, ct) is { } stale)
            {
                return (null, WallEnhancementStepOutcomeKind.Failed, stale);
            }

            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
            watchdog.CancelAfter(StepTimeout);
            try
            {
                return await RunInBatchAsync(run, step, tracker, watchdog.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Wall enhancement {RunId}: step {Step} ran longer than {Timeout} and was stopped", run.Id, step.Key, StepTimeout);
                return (WallEnhancementStepOutcome.Done($"{step.Title} took longer than {StepTimeout.TotalHours:0.#} h and was stopped."),
                    WallEnhancementStepOutcomeKind.Failed, null);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Wall enhancement {RunId}: step {Step} failed; the next steps still run", run.Id, step.Key);
            var text = ex is UserFacingException or UnauthorizedAccessException or InvalidOperationException ? $": {ex.Message}" : ".";
            return (WallEnhancementStepOutcome.Done($"{step.Title} failed{text}"), WallEnhancementStepOutcomeKind.Failed, null);
        }
        finally
        {
            tracker.Clear();
        }
    }

    private async Task<(WallEnhancementStepOutcome? Outcome, WallEnhancementStepOutcomeKind Kind, string? Stale)> RunInBatchAsync(
        WallEnhancementRun run, IWallEnhancementStep step, WallEnhancementStepTracker tracker, CancellationToken ct)
    {
        await using var actors = await actorFactory.CreateAsync(run.CreatedByUserId, ct);
        WallEnhancementStepOutcome outcome;
        using (journal.BeginBatch(WallEnhancementKinds.BatchLabel(step.Key), ChangeJournalScopeKind.Wall, run.WallId))
        {
            outcome = await step.RunAsync(new WallEnhancementStepContext(run.WallId, run.CreatedByUserId, actors, tracker), ct);
        }

        return (outcome, outcome.Skipped ? WallEnhancementStepOutcomeKind.Skipped : WallEnhancementStepOutcomeKind.Done, null);
    }

    private async Task WaitForEditorsAsync(Guid wallId, CancellationToken ct)
    {
        while (editPresence?.IsBeingEdited(wallId) == true)
        {
            await Task.Delay(EditorPoll, ct);
        }
    }
}
