// <copyright file="WallEnhancementSalvage.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// Review items for what steps wrote: after a step returns (the job), and for a step that was stopped from outside while
/// it ran (a cancel, the watchdog, a stale heartbeat), whose writes would otherwise have no item and so survive a discard.
/// </summary>
internal static class WallEnhancementSalvage
{
    /// <summary>
    /// Lists the writes of every step the run's record still shows as running: its batches since it started (under the
    /// labels it recorded) go onto its record and become review items. Saves the items; the caller saves the run.
    /// </summary>
    /// <param name="db">A context tracking <paramref name="run"/>.</param>
    /// <param name="run">The run being stopped.</param>
    /// <param name="ct">Cancellation (callers pass none: the stop must be recorded whole).</param>
    /// <returns>When done.</returns>
    public static async Task SalvageRunningAsync(BlocwerkDbContext db, WallEnhancementRun run, CancellationToken ct)
    {
        foreach (var r in WallEnhancementStepRecord.Parse(run.StepsJson).Where(r => r.Outcome == WallEnhancementStepOutcomeKind.Running).ToList())
        {
            await SalvageStepAsync(db, run, r.Key, ct);
        }
    }

    /// <summary>
    /// Lists one step's writes again, whatever its record says now: for stopped work that went on writing after its stop
    /// was recorded. The batches are merged into the record and new ones become review items.
    /// </summary>
    /// <param name="db">A context tracking <paramref name="run"/>.</param>
    /// <param name="run">The run.</param>
    /// <param name="stepKey">The step.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>When done (the items saved; the caller saves the run).</returns>
    public static async Task SalvageStepAsync(BlocwerkDbContext db, WallEnhancementRun run, string stepKey, CancellationToken ct)
    {
        var records = WallEnhancementStepRecord.Parse(run.StepsJson);
        if (records.FirstOrDefault(r => r.Key == stepKey) is not { Kind: { } kind } record)
        {
            return;
        }

        var labels = record.Labels ?? [WallEnhancementKinds.BatchLabel(record.Key)];
        var found = await WallEnhancementJournal.StepBatchesAsync(db, run.WallId, run.CreatedByUserId, labels, record.StartedAt, ct);
        var batches = record.BatchIds.Concat(found).Distinct().ToList();
        await AddItemsAsync(db, run.Id, kind, batches, [], ct);
        run.StepsJson = WallEnhancementStepRecord.Write(WallEnhancementStepRecord.With(records, record with { BatchIds = batches }));
    }

    /// <summary>
    /// The step's review items: one per hold its batches updated (written kinds) or one per suggestion. An item the run
    /// already has (a resumed step) is not added twice.
    /// </summary>
    public static async Task AddItemsAsync(
        BlocwerkDbContext db,
        Guid runId,
        WallEnhancementItemKind kind,
        IReadOnlyCollection<Guid> batches,
        IReadOnlyList<WallEnhancementSuggestion> suggestions,
        CancellationToken ct)
    {
        var existing = await db.WallEnhancementItems.AsNoTracking()
            .Where(i => i.RunId == runId && i.Kind == kind)
            .Select(i => new { i.HoldId, i.OtherHoldId, i.ReferenceId })
            .ToListAsync(ct);
        var known = existing.Select(e => (e.HoldId, e.OtherHoldId, e.ReferenceId)).ToHashSet();
        var items = WallEnhancementKinds.IsWritten(kind)
            ? await WrittenItemsAsync(db, runId, kind, batches, ct)
            : suggestions.Select(s => new WallEnhancementItem
            {
                RunId = runId, Kind = kind, HoldId = s.HoldId, OtherHoldId = s.OtherHoldId, ReferenceId = s.ReferenceId, Detail = s.Detail,
            }).ToList();

        // A written kind keys on the hold alone: the clean-up's item for a hold the outline step already listed is the same item.
        var fresh = items.Where(i => WallEnhancementKinds.IsWritten(kind)
                ? existing.All(e => e.HoldId != i.HoldId)
                : !known.Contains((i.HoldId, i.OtherHoldId, i.ReferenceId)))
            .ToList();
        await InsertAsync(db, fresh, ct);
    }

    /// <summary>
    /// Saves new items. A written kind's item is unique per run and hold (a filtered unique index): when two stops of one
    /// run salvage at once, the second save hits it, and that item counts as already salvaged. The others are saved one
    /// by one then, each skipped if it is there. Any other failed save is not caught here: it reaches the caller, which
    /// logs it (the job's stop paths, the wall-update guard's caller).
    /// </summary>
    private static async Task InsertAsync(BlocwerkDbContext db, List<WallEnhancementItem> fresh, CancellationToken ct)
    {
        if (fresh.Count == 0)
        {
            return;
        }

        db.WallEnhancementItems.AddRange(fresh);
        try
        {
            await db.SaveChangesAsync(ct);
            return;
        }
        catch (DbUpdateException ex) when (DatabaseErrors.IsUniqueViolation(ex))
        {
            foreach (var item in fresh)
            {
                db.Entry(item).State = EntityState.Detached;
            }
        }

        foreach (var item in fresh)
        {
            db.WallEnhancementItems.Add(item);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (DatabaseErrors.IsUniqueViolation(ex))
            {
                // Already salvaged by the concurrent stop.
                db.Entry(item).State = EntityState.Detached;
            }
        }
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
}
