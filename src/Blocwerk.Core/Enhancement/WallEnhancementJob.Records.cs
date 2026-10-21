// <copyright file="WallEnhancementJob.Records.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Enhancement;

/// <summary>The run's bookkeeping: its row, its step records, its review items.</summary>
public sealed partial class WallEnhancementJob
{
    private async Task<WallEnhancementRun?> LoadAsync(Guid runId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        return await db.WallEnhancementRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
    }

    /// <summary>Writes one step's record (and the run's current step), unless the run stopped being Running meanwhile.</summary>
    private async Task SaveStepAsync(Guid runId, WallEnhancementStepRecord record, string? currentStep, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        var run = await db.WallEnhancementRuns.FirstAsync(r => r.Id == runId, ct);
        if (run.Status != WallEnhancementStatus.Running)
        {
            // Cancelled (or stopped) from outside while the step was still working: its late result is not recorded.
            return;
        }

        run.StepsJson = WallEnhancementStepRecord.Write(WallEnhancementStepRecord.With(WallEnhancementStepRecord.Parse(run.StepsJson), record));
        run.CurrentStep = currentStep;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Why the run must stop before the next step, or null: the wall went, a wall update moved its generation, or one is open.</summary>
    private async Task<string?> StaleReasonAsync(WallEnhancementRun run, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        var generation = await db.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Id == run.WallId)
            .Select(w => (int?)w.CurrentGeneration)
            .FirstOrDefaultAsync(ct);
        return generation switch
        {
            null => "The wall no longer exists.",
            var g when g != run.Generation => "The wall was updated while it was being enhanced. Its new state can be enhanced on its own.",
            _ => await WallEnhancementGuard.OpenUpdateReasonAsync(db, run.WallId, ct),
        };
    }

    private async Task<bool> HasActiveModelAsync(Guid wallId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        return await db.WallGeometryModels.AsNoTracking().AnyAsync(m => m.WallId == wallId && m.IsActive, ct);
    }

    private async Task FinishAsync(Guid runId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        var run = await db.WallEnhancementRuns.FirstAsync(r => r.Id == runId, ct);
        if (run.Status != WallEnhancementStatus.Running)
        {
            return;
        }

        run.Status = WallEnhancementStatus.Review;
        run.CurrentStep = null;
        run.StepsFinishedAt = DateTimeOffset.UtcNow;
        run.UpdatedAt = run.StepsFinishedAt.Value;
        await db.SaveChangesAsync(ct);
    }

    private async Task MarkFailedAsync(Guid runId, string message)
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

            // The writes of a step that was still running get their review items first, so a discard undoes them.
            await WallEnhancementSalvage.SalvageRunningAsync(db, run, CancellationToken.None);
            run.Status = WallEnhancementStatus.Failed;
            run.CurrentStep = null;
            run.Error = message.Length > 500 ? message[..500] : message;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record the failed wall enhancement {RunId}", runId);
        }
    }
}
