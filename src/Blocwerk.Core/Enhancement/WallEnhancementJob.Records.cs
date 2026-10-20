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

    /// <summary>
    /// The step's review items: one per hold its batches updated (written kinds) or one per suggestion. An item the run
    /// already has (a resumed step) is not added twice.
    /// </summary>
    private static async Task AddItemsAsync(
        BlocwerkDbContext db,
        Guid runId,
        IWallEnhancementStep step,
        IReadOnlyCollection<Guid> batches,
        IReadOnlyList<WallEnhancementSuggestion> suggestions,
        CancellationToken ct)
    {
        db.CurrentUserId = Guid.Empty;
        var existing = await db.WallEnhancementItems.AsNoTracking()
            .Where(i => i.RunId == runId && i.Kind == step.Kind)
            .Select(i => new { i.HoldId, i.OtherHoldId, i.ReferenceId })
            .ToListAsync(ct);
        var known = existing.Select(e => (e.HoldId, e.OtherHoldId, e.ReferenceId)).ToHashSet();
        var items = WallEnhancementKinds.IsWritten(step.Kind)
            ? await WrittenItemsAsync(db, runId, step.Kind, batches, ct)
            : suggestions.Select(s => new WallEnhancementItem
            {
                RunId = runId, Kind = step.Kind, HoldId = s.HoldId, OtherHoldId = s.OtherHoldId, ReferenceId = s.ReferenceId, Detail = s.Detail,
            }).ToList();

        // A written kind keys on the hold alone: the clean-up's item for a hold the outline step already listed is the same item.
        var fresh = items.Where(i => WallEnhancementKinds.IsWritten(step.Kind)
                ? existing.All(e => e.HoldId != i.HoldId)
                : !known.Contains((i.HoldId, i.OtherHoldId, i.ReferenceId)))
            .ToList();
        db.WallEnhancementItems.AddRange(fresh);
        await db.SaveChangesAsync(ct);
    }

    private static async Task<List<WallEnhancementItem>> WrittenItemsAsync(
        BlocwerkDbContext db, Guid runId, WallEnhancementItemKind kind, IReadOnlyCollection<Guid> batches, CancellationToken ct)
    {
        var holdIds = (await WallEnhancementJournal.HoldUpdatesAsync(db, batches, ct)).Select(e => e.HoldId).Distinct().ToList();
        var holds = await db.Holds.IgnoreQueryFilters().AsNoTracking()
            .Where(h => holdIds.Contains(h.Id))
            .Select(h => new { h.Id, h.Color, h.StackedOnHoldId })
            .ToListAsync(ct);
        return holds.Select(h => new WallEnhancementItem
        {
            RunId = runId,
            Kind = kind,
            HoldId = h.Id,
            OtherHoldId = kind == WallEnhancementItemKind.Stack ? h.StackedOnHoldId : null,
            Detail = kind == WallEnhancementItemKind.Colour ? h.Color : null,
        }).ToList();
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

            run.Status = WallEnhancementStatus.Failed;
            run.CurrentStep = null;
            run.Error = message.Length > 500 ? message[..500] : message;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record the failed wall enhancement {RunId}", runId);
        }
    }
}
