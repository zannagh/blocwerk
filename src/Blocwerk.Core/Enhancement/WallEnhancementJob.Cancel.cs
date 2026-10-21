// <copyright file="WallEnhancementJob.Cancel.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Enhancement;

/// <summary>Cancelling a run and its heartbeat: what keeps a hung run from blocking the wall.</summary>
public sealed partial class WallEnhancementJob
{
    /// <summary>
    /// An admin cancelled the run: the step in hand is recorded as failed ("Cancelled") and the run waits for its review,
    /// where that step can be retried or the run discarded. Also used when the job is not live in this process (its
    /// heartbeat stopped), so a hung run never blocks the wall.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <returns>When recorded.</returns>
    public async Task MarkCancelledAsync(Guid runId)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;
            var run = await db.WallEnhancementRuns.FirstOrDefaultAsync(r => r.Id == runId);
            if (run is not { Status: WallEnhancementStatus.Running })
            {
                return;
            }

            // The step in hand may have written already: its batches become review items before it is failed.
            await WallEnhancementSalvage.SalvageRunningAsync(db, run, CancellationToken.None);
            var during = run.CurrentStep;
            var records = CancelledRecords(WallEnhancementStepRecord.Parse(run.StepsJson), during, DateTimeOffset.UtcNow);
            var now = DateTimeOffset.UtcNow;
            run.StepsJson = WallEnhancementStepRecord.Write(records);
            (run.Status, run.CurrentStep, run.StepsFinishedAt, run.UpdatedAt) = (WallEnhancementStatus.Review, null, now, now);
            await db.SaveChangesAsync();
            logger.LogInformation("Wall enhancement {RunId} cancelled during step {Step}", runId, (object?)during ?? "none (between steps or queued)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record the cancelled wall enhancement {RunId}", runId);
        }
    }

    /// <summary>
    /// Every step a cancel leaves unfinished, recorded as failed: the one in hand "was cancelled", every later one "was
    /// cancelled before it ran" (<see cref="WallEnhancementStepRecord.NeverRan"/>). So a cancel between steps, while waiting
    /// for an editor or while queued never leaves an empty review that looks complete: Finalize refuses failed steps, and
    /// a retry runs them.
    /// </summary>
    /// <param name="records">The run's records.</param>
    /// <param name="current">The step that was running, if any.</param>
    /// <param name="now">The clock.</param>
    /// <returns>The records with every unfinished step failed.</returns>
    internal List<WallEnhancementStepRecord> CancelledRecords(List<WallEnhancementStepRecord> records, string? current, DateTimeOffset now)
    {
        foreach (var step in ordered.Where(s => records.FirstOrDefault(r => r.Key == s.Key) is not { IsFinished: true }))
        {
            var recorded = records.FirstOrDefault(r => r.Key == step.Key);
            var ran = step.Key == current || recorded is not null;
            records = WallEnhancementStepRecord.With(records, new WallEnhancementStepRecord(
                step.Key,
                step.Title,
                WallEnhancementStepOutcomeKind.Failed,
                ran ? $"{step.Title} was cancelled." : $"{step.Title} was cancelled before it ran.",
                recorded?.StartedAt ?? now,
                now,
                recorded?.BatchIds ?? [],
                NeverRan: !ran,
                Kind: step.Kind,
                Labels: recorded?.Labels ?? WallEnhancementJournal.StepLabels(step)));
        }

        return records;
    }

    /// <summary>Lists a stopped step's writes once more, after its work returned or was let go (whatever the run's status).</summary>
    /// <param name="runId">The run.</param>
    /// <param name="stepKey">The step that was in hand.</param>
    /// <returns>When done.</returns>
    internal async Task ResalvageAsync(Guid runId, string stepKey)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;
            if (await db.WallEnhancementRuns.FirstOrDefaultAsync(r => r.Id == runId) is { } run)
            {
                await WallEnhancementSalvage.SalvageStepAsync(db, run, stepKey, CancellationToken.None);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Wall enhancement {RunId}: the writes of stopped step {Step} could not be listed again", runId, stepKey);
        }
    }

    /// <summary>Renews the run's heartbeat until the run ends (the wall-update guard reads it).</summary>
    internal async Task HeartbeatAsync(Guid runId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // One failed save (a dropped connection) must not end the heartbeat: the run would look hung ten minutes later
            // and a wall update could start while the job still writes.
            try
            {
                await BeatAsync(runId, ct);
                await Task.Delay(HeartbeatInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Wall enhancement {RunId}: a heartbeat could not be saved; trying again", runId);
                await DelayQuietlyAsync(HeartbeatInterval, ct);
            }
        }
    }

    /// <summary>One heartbeat: the run's <see cref="WallEnhancementRun.HeartbeatAt"/>, while it is Running.</summary>
    internal async Task BeatAsync(Guid runId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        if (await db.WallEnhancementRuns.FirstOrDefaultAsync(r => r.Id == runId, ct) is { Status: WallEnhancementStatus.Running } run)
        {
            run.HeartbeatAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task DelayQuietlyAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // The run ended; the loop sees it.
        }
    }
}
