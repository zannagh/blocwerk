// <copyright file="WallEnhancementService.Lifecycle.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Enhancement;

/// <summary>A run's second chances: retrying a failed step, and discarding the whole run.</summary>
public sealed partial class WallEnhancementService
{
    /// <summary>How long a cancel waits for the job to stop before it marks the run itself.</summary>
    public static readonly TimeSpan CancelWait = TimeSpan.FromSeconds(10);

    /// <inheritdoc/>
    public async Task<WallEnhancementView> CancelAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var run = await CurrentRunAsync(db, wallId, ct);
            if (run is not { Status: WallEnhancementStatus.Running })
            {
                throw new UserFacingException("No enhancement of this wall is running.");
            }

            logger.LogInformation("Wall enhancement {RunId} cancelled by {UserId}", run.Id, userId);
            if (runner.Cancel(run.Id))
            {
                await Task.WhenAny(runner.WhenIdleAsync(run.Id), Task.Delay(CancelWait, ct));
            }

            // Not live here, or still stuck in a step that ignores cancellation: recorded directly. Its late result is
            // then dropped (the job records a step only while the run is Running).
            await runner.Job.MarkCancelledAsync(run.Id);
            return await BuildViewAsync(db, wallId, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<WallEnhancementView> RetryStepAsync(Guid wallId, string stepKey, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var run = await CurrentRunAsync(db, wallId, ct);
            if (run is not { Status: WallEnhancementStatus.Review or WallEnhancementStatus.Failed })
            {
                throw new UserFacingException("Only a step of an enhancement that waits for its review can be run again.");
            }

            if (await BlockedReasonAsync(db, wallId, ct) is { } blocked)
            {
                throw new UserFacingException(blocked);
            }

            if (runner.IsRunning(run.Id))
            {
                throw new UserFacingException("The cancelled step is still finishing. Try again in a moment.");
            }

            var records = WallEnhancementStepRecord.Parse(run.StepsJson);
            var failed = records.FirstOrDefault(r => r.Key == stepKey && r.Outcome == WallEnhancementStepOutcomeKind.Failed)
                         ?? throw new UserFacingException("That step did not fail, so there is nothing to retry.");

            // Unfinished again, with its first start and batches kept: what it wrote before failing still counts.
            var again = failed with { Outcome = WallEnhancementStepOutcomeKind.Running, FinishedAt = null };
            run.StepsJson = WallEnhancementStepRecord.Write(WallEnhancementStepRecord.With(records, again));
            run.Status = WallEnhancementStatus.Running;
            run.Error = null;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Wall enhancement {RunId}: step {Step} retried by {UserId}", run.Id, stepKey, userId);
            runner.Launch(run.Id);
            return await BuildViewAsync(db, wallId, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<WallEnhancementDiscardResult> DiscardAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var run = await CurrentRunAsync(db, wallId, ct);
            if (run is not { Status: WallEnhancementStatus.Review or WallEnhancementStatus.Failed })
            {
                throw new UserFacingException(run?.Status switch
                {
                    WallEnhancementStatus.Running => "The enhancement is still running. Discard it once its steps are done.",
                    WallEnhancementStatus.Finalized => "A finalized enhancement cannot be discarded.",
                    _ => "There is no enhancement of this wall to discard.",
                });
            }

            var items = await db.WallEnhancementItems.Where(i => i.RunId == run.Id).ToListAsync(ct);
            var (answersBack, answersKept) = await RevertAnswersAsync(wallId, items, ct);
            var records = await RestoreRecordsAsync(wallId, items, ct);
            var (undone, kept) = await UndoWrittenAsync(run, items, userId, ct);

            // The run row goes (its items with it): the generation counts as not enhanced, and the button works again.
            db.WallEnhancementRuns.Remove(run);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Wall enhancement {RunId} of wall {WallId} discarded by {UserId}: {Undone} holds restored, {Answers} answers taken back, {Kept} left as they are",
                run.Id, wallId, userId, undone, answersBack, kept + answersKept);
            return new WallEnhancementDiscardResult(undone, answersBack, kept + answersKept, records);
        }
    }

    /// <summary>Takes back what the answers wrote (merges, links, added holds, confirmed colours), newest first.</summary>
    private async Task<(int Back, int Kept)> RevertAnswersAsync(Guid wallId, List<WallEnhancementItem> items, CancellationToken ct)
    {
        var batches = items.Where(i => i is { Decision: WallEnhancementDecision.Accepted, ResultBatchId: not null })
            .OrderByDescending(i => i.DecidedAt)
            .Select(i => i.ResultBatchId!.Value)
            .Distinct()
            .ToList();
        var (back, kept) = (0, 0);
        foreach (var batchId in batches)
        {
            ChangeJournalRevertResult result;
            using (await WallHoldWriteLock.AcquireAsync(wallId, ct: ct))
            {
                result = await reverter.RevertBatchAsync(batchId, force: false, ct);
            }

            (back, kept) = result.Reverted ? (back + 1, kept) : (back, kept + 1);
        }

        return (back, kept);
    }

    /// <summary>Undoes every hold the steps wrote that is not undone yet (only where nobody changed it since).</summary>
    private async Task<(int Undone, int Kept)> UndoWrittenAsync(WallEnhancementRun run, List<WallEnhancementItem> items, Guid userId, CancellationToken ct)
    {
        var (undone, kept) = (0, 0);
        foreach (var item in items.Where(i => WallEnhancementKinds.IsWritten(i.Kind) && i.Decision != WallEnhancementDecision.Rejected))
        {
            var (decision, _) = await UndoAsync(run, item, userId, ct);
            (undone, kept) = decision == WallEnhancementDecision.Rejected ? (undone + 1, kept) : (undone, kept + 1);
        }

        return (undone, kept);
    }

    private static async Task<WallEnhancementRun?> CurrentRunAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var generation = await GenerationAsync(db, wallId, ct);
        return await db.WallEnhancementRuns.FirstOrDefaultAsync(r => r.WallId == wallId && r.Generation == generation, ct);
    }
}
