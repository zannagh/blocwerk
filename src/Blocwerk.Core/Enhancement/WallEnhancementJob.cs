// <copyright file="WallEnhancementJob.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

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
/// a run interrupted by a restart resumes with exactly the step that was cut off (all steps are idempotent). A step that
/// throws is recorded as failed and the next one still runs; the run then waits for its review.
/// </summary>
/// <remarks>
/// Before each step the job waits while someone has the wall's hold editor open (<see cref="IWallEditPresence"/>) and
/// while another writer holds the wall's hold lock; during the step it holds the deploy gate. It stops (Failed,
/// resumable) when the wall's generation moved: the run belongs to the generation it started on.
/// </remarks>
public sealed partial class WallEnhancementJob(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    IWallEnhancementActorFactory actorFactory,
    IEnumerable<IWallEnhancementStep> steps,
    IChangeJournal journal,
    ILogger<WallEnhancementJob> logger,
    IDeployBusyGate? busyGate = null,
    IWallEditPresence? editPresence = null)
{
    /// <summary>How long the job waits for another hold writer of the wall before the step fails.</summary>
    public static readonly TimeSpan WriterWait = TimeSpan.FromMinutes(10);

    private readonly IReadOnlyList<IWallEnhancementStep> ordered = steps.OrderBy(s => s.Order).ToList();

    /// <summary>How often the job looks again while an editor has the wall open.</summary>
    public TimeSpan EditorPoll { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The steps in the order they run.</summary>
    public IReadOnlyList<IWallEnhancementStep> Steps => ordered;

    /// <summary>Runs (or resumes) the run. Never throws but on shutdown.</summary>
    /// <param name="runId">The run; it must be <see cref="WallEnhancementStatus.Running"/>.</param>
    /// <param name="ct">Cancellation (app shutdown: the run stays Running and resumes on the next start).</param>
    /// <returns>When it stopped.</returns>
    public async Task RunAsync(Guid runId, CancellationToken ct)
    {
        try
        {
            await RunCoreAsync(runId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: the run stays Running and the resumer continues it.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Wall enhancement {RunId} failed", runId);
            await MarkFailedAsync(runId, ex.Message);
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

            if (await StaleReasonAsync(run, ct) is { } why)
            {
                await MarkFailedAsync(runId, why);
                return;
            }

            await WaitForEditorsAsync(run.WallId, ct);
            await RunOneAsync(run, step, recorded?.StartedAt ?? DateTimeOffset.UtcNow, ct);
        }

        await FinishAsync(runId, ct);
    }

    private async Task RunOneAsync(WallEnhancementRun run, IWallEnhancementStep step, DateTimeOffset started, CancellationToken ct)
    {
        var running = new WallEnhancementStepRecord(step.Key, step.Title, WallEnhancementStepOutcomeKind.Running, string.Empty, started, null, []);
        await SaveStepAsync(run.Id, running, step.Key, ct);
        var (outcome, kind) = await RunStepAsync(run, step, ct);
        List<Guid> batches;
        await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            batches = await WallEnhancementJournal.StepBatchesAsync(db, run.WallId, run.CreatedByUserId, step, started, ct);
            await AddItemsAsync(db, run.Id, step, batches, outcome.Suggestions, ct);
        }

        var done = running with { Outcome = kind, Summary = outcome.Summary, FinishedAt = DateTimeOffset.UtcNow, BatchIds = batches };
        await SaveStepAsync(run.Id, done, null, ct);
        logger.LogInformation(
            "Wall enhancement {RunId} on wall {WallId}, step {Step}: {Outcome} — {Summary} ({Batches} journal batches)",
            run.Id, run.WallId, step.Key, kind, outcome.Summary, batches.Count);
    }

    private async Task<(WallEnhancementStepOutcome Outcome, WallEnhancementStepOutcomeKind Kind)> RunStepAsync(
        WallEnhancementRun run, IWallEnhancementStep step, CancellationToken ct)
    {
        using var busy = busyGate?.Hold(DeployBusyWork.WallEnhancement, run.WallId, run.CreatedByUserId);
        try
        {
            if (step.NeedsModel && !await HasActiveModelAsync(run.WallId, ct))
            {
                return (WallEnhancementStepOutcome.Skip("This wall has no 3D model yet."), WallEnhancementStepOutcomeKind.Skipped);
            }

            // Another writer of the wall's holds (a clean-up, a merge, a wall-update commit) finishes first: the steps
            // themselves fail fast on the lock, which in the background would only mean a failed step.
            (await WallHoldWriteLock.AcquireAsync(run.WallId, WriterWait, ct)).Dispose();
            await using var actors = await actorFactory.CreateAsync(run.CreatedByUserId, ct);
            WallEnhancementStepOutcome outcome;
            using (journal.BeginBatch(WallEnhancementKinds.BatchLabel(step.Key), ChangeJournalScopeKind.Wall, run.WallId))
            {
                outcome = await step.RunAsync(new WallEnhancementStepContext(run.WallId, run.CreatedByUserId, actors), ct);
            }

            return (outcome, outcome.Skipped ? WallEnhancementStepOutcomeKind.Skipped : WallEnhancementStepOutcomeKind.Done);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Wall enhancement {RunId}: step {Step} failed; the next steps still run", run.Id, step.Key);
            var text = ex is UserFacingException or UnauthorizedAccessException or InvalidOperationException ? $": {ex.Message}" : ".";
            return (WallEnhancementStepOutcome.Done($"{step.Title} failed{text}"), WallEnhancementStepOutcomeKind.Failed);
        }
    }

    private async Task WaitForEditorsAsync(Guid wallId, CancellationToken ct)
    {
        while (editPresence?.IsBeingEdited(wallId) == true)
        {
            await Task.Delay(EditorPoll, ct);
        }
    }
}
