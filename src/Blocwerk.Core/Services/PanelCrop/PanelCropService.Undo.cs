// <copyright file="PanelCropService.Undo.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services.PanelCrop;

/// <summary>A journalled crop of one panel: its batch and how many holds it removed.</summary>
/// <param name="Id">The <c>panel-crop</c> batch.</param>
/// <param name="RemovedHolds">How many holds it deleted.</param>
internal sealed record PanelCropBatch(Guid Id, int RemovedHolds);

/// <summary>
/// "Undo crop". Preferred path: revert the crop's journal batches (the current crop and any re-crops on top of it,
/// newest first) with <see cref="ChangeJournalReverter"/>, which also re-inserts the holds the crop removed, their links
/// and boulder memberships, and puts the boulders' historic flag back. The reverter refuses a batch whose rows were
/// changed since (a hold edited after the crop, say); then whatever is still cropped is undone photo-only: the original
/// photo comes back and the holds are mapped back, but removed holds stay removed (reported in the result).
/// </summary>
public sealed partial class PanelCropService
{
    private const string CropLabel = "panel-crop";
    private const string UndoLabel = "panel-crop-undo";

    /// <inheritdoc/>
    public async Task<PanelCropResult> UndoAsync(Guid wallId, Guid panelId, CancellationToken ct = default)
    {
        List<PanelCropBatch> chain;
        await using (var db = await OpenAdminContextAsync(wallId, ct))
        {
            var target = await LoadTargetAsync(db, wallId, panelId, ct);
            if (target.Crop is null)
            {
                throw new InvalidOperationException("This panel photo is not cropped.");
            }

            chain = await CropChainAsync(db, wallId, panelId, ct);
        }

        var reverted = await RevertChainAsync(chain, ct);
        await using var after = await OpenAdminContextAsync(wallId, ct);
        var rest = await LoadTargetAsync(after, wallId, panelId, ct);
        if (rest.Crop is null)
        {
            var holdIds = await after.Holds.Where(h => h.WallPanelId == panelId && h.Generation == rest.Panel.Generation)
                .Select(h => h.Id).ToListAsync(ct);
            refinementQueue?.Enqueue(wallId, holdIds);
            logger.LogInformation("Panel {PanelId} on wall {WallId}: crop reverted from the journal ({Batches} batches)", panelId, wallId, reverted);
            return new PanelCropResult(true, new PanelCropPreview([], 0, []), rest.Panel.PhotoRevision, 0, true, 0);
        }

        var notRestored = chain.Skip(reverted).Sum(b => b.RemovedHolds);
        return await UndoPhotoOnlyAsync(after, rest, notRestored, ct);
    }

    /// <summary>
    /// The current crop's batches for this panel, newest first: recorded (not reverted) <c>panel-crop</c> batches that
    /// wrote its <see cref="WallPanelCrop"/> row since the last photo-only undo of it.
    /// </summary>
    private static async Task<List<PanelCropBatch>> CropChainAsync(BlocwerkDbContext db, Guid wallId, Guid panelId, CancellationToken ct)
    {
        var key = panelId.ToString();
        var cropEntity = nameof(WallPanelCrop);
        var batchIds = await db.ChangeJournalEntries.AsNoTracking()
            .Where(e => e.EntityType == cropEntity && e.KeyJson.Contains(key))
            .Select(e => e.BatchId).Distinct().ToListAsync(ct);
        var batches = await db.ChangeJournalBatches.AsNoTracking()
            .Where(b => batchIds.Contains(b.Id) && b.ScopeId == wallId)
            .Select(b => new { b.Id, b.Label, b.Status, b.CreatedAt })
            .ToListAsync(ct);

        // Ordered in memory: SQLite cannot order by a DateTimeOffset column.
        var lastUndo = batches.Where(b => b.Label == UndoLabel).Select(b => b.CreatedAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        var chain = batches
            .Where(b => b.Label == CropLabel && b.Status == ChangeJournalStatus.Recorded && b.CreatedAt > lastUndo)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => b.Id)
            .ToList();
        var holdEntity = nameof(Hold);
        var deletes = await db.ChangeJournalEntries.AsNoTracking()
            .Where(e => chain.Contains(e.BatchId) && e.EntityType == holdEntity && e.Op == ChangeJournalOp.Delete)
            .Select(e => e.BatchId)
            .ToListAsync(ct);
        return chain.Select(id => new PanelCropBatch(id, deletes.Count(d => d == id))).ToList();
    }

    /// <summary>Reverts the chain newest first, stopping at the first batch the journal refuses. Returns how many were reverted.</summary>
    private async Task<int> RevertChainAsync(List<PanelCropBatch> chain, CancellationToken ct)
    {
        if (reverter is null)
        {
            return 0;
        }

        var reverted = 0;
        foreach (var batch in chain)
        {
            var result = await reverter.RevertBatchAsync(batch.Id, cancellationToken: ct);
            if (!result.Reverted)
            {
                logger.LogInformation(
                    "Crop batch {BatchId} cannot be reverted ({Conflicts} conflicts: {Error}); falling back to a photo-only undo",
                    batch.Id, result.Conflicts.Count, result.Error);
                break;
            }

            reverted++;
        }

        return reverted;
    }

    /// <summary>The original photo back and the holds mapped back onto it; holds the crop removed stay removed.</summary>
    private async Task<PanelCropResult> UndoPhotoOnlyAsync(BlocwerkDbContext db, CropTarget target, int notRestored, CancellationToken ct)
    {
        var crop = target.Crop!;
        var map = PanelFrameMap.OutOfCrop(new PanelCropRect(crop.Left, crop.Top, crop.Width, crop.Height));
        target.Panel.Photo = crop.OriginalPhoto;
        target.Panel.PhotoContentType = crop.OriginalPhotoContentType;
        target.Panel.PhotoRevision++;
        db.WallPanelCrops.Remove(crop);

        await ApplyAsync(db, target, map, [], UndoLabel, ct);
        logger.LogInformation(
            "Panel {PanelId} on wall {WallId}: crop undone photo-only, original photo restored, {Lost} removed holds not restored",
            target.Panel.Id, target.Panel.WallId, notRestored);
        return new PanelCropResult(true, new PanelCropPreview([], 0, []), target.Panel.PhotoRevision, 0, false, notRestored);
    }
}
