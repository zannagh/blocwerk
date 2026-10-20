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

            var records = WallEnhancementStepRecord.Parse(run.StepsJson);
            var now = DateTimeOffset.UtcNow;
            if (run.CurrentStep is { } key && ordered.FirstOrDefault(s => s.Key == key) is { } step)
            {
                var started = records.FirstOrDefault(r => r.Key == key)?.StartedAt ?? now;
                var batches = records.FirstOrDefault(r => r.Key == key)?.BatchIds ?? [];
                records = WallEnhancementStepRecord.With(records, new WallEnhancementStepRecord(
                    key, step.Title, WallEnhancementStepOutcomeKind.Failed, $"{step.Title} was cancelled.", started, now, batches));
            }

            run.StepsJson = WallEnhancementStepRecord.Write(records);
            (run.Status, run.CurrentStep, run.StepsFinishedAt, run.UpdatedAt) = (WallEnhancementStatus.Review, null, now, now);
            await db.SaveChangesAsync();
            logger.LogInformation("Wall enhancement {RunId} cancelled during step {Step}", runId, records.LastOrDefault()?.Key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record the cancelled wall enhancement {RunId}", runId);
        }
    }

    /// <summary>Renews the run's heartbeat until the run ends (the wall-update guard reads it).</summary>
    internal async Task HeartbeatAsync(Guid runId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
                {
                    db.CurrentUserId = Guid.Empty;
                    if (await db.WallEnhancementRuns.FirstOrDefaultAsync(r => r.Id == runId, ct) is { Status: WallEnhancementStatus.Running } run)
                    {
                        run.HeartbeatAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync(ct);
                    }
                }

                await Task.Delay(HeartbeatInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Wall enhancement {RunId}: the heartbeat stopped", runId);
        }
    }
}
