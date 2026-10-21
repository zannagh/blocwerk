// <copyright file="WallEnhancementService.CropGone.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// The review picture of a hold that is gone since the run (an accepted duplicate merged it away): an answered card
/// still shows what was decided, drawn from the hold's last row in the change journal instead of a 404.
/// </summary>
public sealed partial class WallEnhancementService
{
    /// <summary>
    /// The hold as it was when it was deleted, from its newest journalled delete in this wall's batches since the run
    /// started (the item's own result batch first), or null when none is recorded there.
    /// </summary>
    private async Task<Hold?> DeletedHoldAsync(WallEnhancementRun run, WallEnhancementItem item, Guid holdId, CancellationToken ct)
    {
        await using var scratch = await dbContextFactory.CreateDbContextAsync(ct);
        var batchIds = await BatchesSinceRunAsync(scratch, run, item, ct);
        if (batchIds.Count == 0)
        {
            return null;
        }

        var needle = holdId.ToString();
        var deletes = await scratch.ChangeJournalEntries.AsNoTracking()
            .Where(e => batchIds.Contains(e.BatchId) && e.EntityType == nameof(Hold) && e.Op == ChangeJournalOp.Delete && e.BeforeJson != null)
            .Where(e => e.KeyJson.Contains(needle))
            .ToListAsync(ct);
        var last = deletes.Where(e => WallEnhancementJournal.HoldIdOf(e.KeyJson) == holdId)
            .OrderBy(e => batchIds.IndexOf(e.BatchId))
            .FirstOrDefault();
        var entityType = scratch.Model.FindEntityType(typeof(Hold));
        if (last is null || entityType is null)
        {
            return null;
        }

        var hold = new Hold();
        ChangeJournalEntityAccessor.ApplyValues(scratch.Entry(hold), ChangeJournalValueWriter.DeserializeValues(entityType, last.BeforeJson!, _ => null));
        return hold.WallId == run.WallId ? hold : null;
    }

    /// <summary>The item's result batch, then the wall's batches created since the run started, newest first.</summary>
    private static async Task<List<Guid>> BatchesSinceRunAsync(BlocwerkDbContext db, WallEnhancementRun run, WallEnhancementItem item, CancellationToken ct)
    {
        var rows = await db.ChangeJournalBatches.AsNoTracking()
            .Where(b => b.ScopeKind == ChangeJournalScopeKind.Wall && b.ScopeId == run.WallId)
            .Select(b => new { b.Id, b.CreatedAt })
            .ToListAsync(ct);

        // Compared in memory: SQLite cannot compare DateTimeOffset columns. A second of slack for clock rounding.
        var from = run.CreatedAt.AddSeconds(-1);
        var ids = rows.Where(b => b.CreatedAt >= from).OrderByDescending(b => b.CreatedAt).Select(b => b.Id).ToList();
        if (item.ResultBatchId is { } own && ids.Remove(own))
        {
            ids.Insert(0, own);
        }

        return ids;
    }
}
