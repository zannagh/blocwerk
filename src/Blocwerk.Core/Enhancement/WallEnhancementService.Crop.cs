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
/// hold as it was before the enhancement — dashed in magenta (<see cref="HoldPairCrop"/>). A pair whose holds sit on
/// two panels (every link suggestion) gets a second picture, of the other hold on its own photo.
/// </summary>
public sealed partial class WallEnhancementService
{
    /// <inheritdoc/>
    public Task<byte[]?> CropAsync(Guid wallId, Guid itemId, CancellationToken ct = default) =>
        RenderAsync(wallId, itemId, otherSide: false, ct);

    /// <inheritdoc/>
    public Task<byte[]?> OtherCropAsync(Guid wallId, Guid itemId, CancellationToken ct = default) =>
        RenderAsync(wallId, itemId, otherSide: true, ct);

    /// <summary>
    /// The picture of the item's hold (or, for <paramref name="otherSide"/>, of its other hold) on that hold's own panel
    /// photo, with the pair's other hold dashed when it sits on the same photo.
    /// </summary>
    private async Task<byte[]?> RenderAsync(Guid wallId, Guid itemId, bool otherSide, CancellationToken ct)
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
                return !otherSide && found.Item.ReferenceId is { } proposalId ? await proposals.CropAsync(wallId, proposalId, ct) : null;
            }

            var shownId = otherSide ? found.Item.OtherHoldId : found.Item.HoldId;
            var shown = shownId is { } holdId
                ? await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId && h.WallId == wallId, ct) ?? await DeletedHoldAsync(wallId, holdId, ct)
                : null;
            if (shown?.WallPanelId is not { } panelId)
            {
                return null;
            }

            var dashed = otherSide ? await PartnerAsync(db, found.Item.HoldId, shown, ct) : await OtherAsync(db, found.Run, found.Item, shown, ct);
            var photo = await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId && p.WallId == wallId).Select(p => p.Photo).FirstOrDefaultAsync(ct);
            return photo is null ? null : await Task.Run(() => HoldPairCrop.Render(photo, shown, dashed), ct);
        }
    }

    /// <summary>What the picture shows next to the hold: the pair's other hold on the same photo, the old shape, or nothing.</summary>
    private async Task<Hold> OtherAsync(BlocwerkDbContext db, WallEnhancementRun run, WallEnhancementItem item, Hold focus, CancellationToken ct)
    {
        if (item.Kind == WallEnhancementItemKind.Shape)
        {
            return await BeforeAsync(run, focus, ct) ?? focus;
        }

        return await PartnerAsync(db, item.OtherHoldId, focus, ct);
    }

    /// <summary>The pair's other hold when it sits on the same photo as <paramref name="shown"/>; otherwise the hold itself.</summary>
    private static async Task<Hold> PartnerAsync(BlocwerkDbContext db, Guid? partnerId, Hold shown, CancellationToken ct)
    {
        if (partnerId is not { } id)
        {
            return shown;
        }

        var partner = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, ct);
        return partner is not null && partner.WallPanelId == shown.WallPanelId ? partner : shown;
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
