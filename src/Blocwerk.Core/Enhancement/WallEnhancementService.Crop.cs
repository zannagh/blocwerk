// <copyright file="WallEnhancementService.Crop.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// The review pictures: the hold on its panel photo in yellow, with the other hold of a pair — or, for a shape, the
/// hold as it was before the enhancement — dashed in magenta (<see cref="HoldPairCrop"/>).
/// </summary>
public sealed partial class WallEnhancementService
{
    /// <inheritdoc/>
    public async Task<byte[]?> CropAsync(Guid wallId, Guid itemId, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var found = await db.WallEnhancementItems.AsNoTracking()
                .Where(i => i.Id == itemId && i.Run.WallId == wallId)
                .Select(i => new { Item = i, i.Run })
                .FirstOrDefaultAsync(ct);
            if (found is null)
            {
                return null;
            }

            if (found.Item.Kind == WallEnhancementItemKind.NewHold)
            {
                return found.Item.ReferenceId is { } proposalId ? await proposals.CropAsync(wallId, proposalId, ct) : null;
            }

            var focus = found.Item.HoldId is { } holdId ? await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId && h.WallId == wallId, ct) : null;
            if (focus?.WallPanelId is not { } panelId)
            {
                return null;
            }

            var other = await OtherAsync(db, found.Run, found.Item, focus, ct);
            var photo = await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId && p.WallId == wallId).Select(p => p.Photo).FirstOrDefaultAsync(ct);
            return photo is null ? null : await Task.Run(() => HoldPairCrop.Render(photo, focus, other), ct);
        }
    }

    /// <summary>What the picture shows next to the hold: the pair's other hold on the same photo, the old shape, or nothing.</summary>
    private async Task<Hold> OtherAsync(BlocwerkDbContext db, WallEnhancementRun run, WallEnhancementItem item, Hold focus, CancellationToken ct)
    {
        if (item.Kind == WallEnhancementItemKind.Shape)
        {
            return await BeforeAsync(run, focus, ct) ?? focus;
        }

        if (item.OtherHoldId is not { } otherId)
        {
            return focus;
        }

        var other = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == otherId, ct);
        return other is not null && other.WallPanelId == focus.WallPanelId ? other : focus;
    }

    /// <summary>
    /// The hold as it was before the shape steps: its current row with every shape batch's before-values put back,
    /// newest first, on a context that is never saved.
    /// </summary>
    private async Task<Hold?> BeforeAsync(WallEnhancementRun run, Hold current, CancellationToken ct)
    {
        var batches = BatchesNewestFirst(run, WallEnhancementItemKind.Shape).ToList();
        await using var scratch = await dbContextFactory.CreateDbContextAsync(ct);
        var needle = current.Id.ToString();
        var entries = await scratch.ChangeJournalEntries.AsNoTracking()
            .Where(e => batches.Contains(e.BatchId) && e.EntityType == nameof(Hold) && e.Op == ChangeJournalOp.Update && e.KeyJson.Contains(needle))
            .ToListAsync(ct);
        var mine = entries.Where(e => WallEnhancementJournal.HoldIdOf(e.KeyJson) == current.Id)
            .OrderBy(e => batches.IndexOf(e.BatchId)).ThenByDescending(e => e.Seq)
            .ToList();
        var hold = await scratch.Holds.FirstOrDefaultAsync(h => h.Id == current.Id, ct);
        var entityType = scratch.Model.FindEntityType(typeof(Hold));
        if (mine.Count == 0 || hold is null || entityType is null)
        {
            return null;
        }

        var entry = scratch.Entry(hold);
        foreach (var e in mine)
        {
            ChangeJournalEntityAccessor.ApplyValues(entry, ChangeJournalValueWriter.DeserializeValues(entityType, e.BeforeJson ?? "{}", _ => null));
        }

        return hold;
    }
}
