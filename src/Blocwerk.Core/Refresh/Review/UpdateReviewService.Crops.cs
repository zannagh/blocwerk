// <copyright file="UpdateReviewService.Crops.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh.Review;

/// <summary>
/// The pictures of a review item, made on request and kept in a small cache (what they show is fixed while the update
/// is open). A card's pictures are the confirm screen's. An old hold: "old" around it on its live photo, "new" at the
/// spot the matcher puts it on the new photo (its old spot when the match is not at hand). A detection: "new" only; an
/// old picture at the same coordinates could show another place, so there is none.
/// </summary>
public sealed partial class UpdateReviewService
{
    private const int MaxCached = 512;
    private static readonly ConcurrentDictionary<(Guid Session, string Key, CheckCropView View), byte[]?> Crops = new();

    public async Task<byte[]?> GetCropAsync(Guid refreshId, string key, CheckCropView view, CancellationToken ct)
    {
        var (db, refresh, _) = await OpenAsync(refreshId);
        await using (db)
        {
            if (refresh.UpdateSessionId is not { } sessionId)
            {
                return null;
            }

            var (prefix, id) = ParseKey(key);
            if (prefix == 'c')
            {
                return await RefreshCheckCrops.GetAsync(dbContextFactory, files, sessionId, id, view, ct);
            }

            if (Crops.TryGetValue((sessionId, key, view), out var cached))
            {
                return cached;
            }

            var bytes = await MakeCropAsync(db, refresh.WallId, prefix, id, view);
            if (Crops.Count >= MaxCached)
            {
                Crops.Clear();
            }

            Crops[(sessionId, key, view)] = bytes;
            return bytes;
        }
    }

    private async Task<byte[]?> MakeCropAsync(BlocwerkDbContext db, Guid wallId, char prefix, Guid id, CheckCropView view)
    {
        if (prefix == 'r')
        {
            var suggestion = await db.WallUpdateRelocationProposals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            return suggestion is null ? null
                : view == CheckCropView.Old ? await OldCropAsync(db, suggestion.OldHoldId) : await StagedCropAsync(db, suggestion.NewHoldId);
        }

        if (prefix is 'n' or 'd')
        {
            return view == CheckCropView.New ? await StagedCropAsync(db, id) : null;
        }

        return view switch
        {
            CheckCropView.Old => await OldCropAsync(db, id),
            CheckCropView.New => await SpotCropAsync(db, wallId, id),
            _ => null,
        };
    }

    private static async Task<byte[]?> OldCropAsync(BlocwerkDbContext db, Guid holdId)
    {
        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId);
        if (hold is null)
        {
            return null;
        }

        var photo = hold.WallPanelId is { } panelId
            ? await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId).Select(p => p.Photo).FirstOrDefaultAsync()
            : null;
        photo ??= await db.Walls.AsNoTracking().IgnoreQueryFilters().Where(w => w.Id == hold.WallId).Select(w => w.Photo).FirstOrDefaultAsync();
        return RefreshCheckCrops.Crop(photo, hold.X, hold.Y, hold.Radius).Bytes;
    }

    private static async Task<byte[]?> StagedCropAsync(BlocwerkDbContext db, Guid holdId)
    {
        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId);
        if (hold is not { WallPanelId: { } panelId })
        {
            return null;
        }

        var photo = await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId).Select(p => p.StagedPhoto).FirstOrDefaultAsync();
        return RefreshCheckCrops.Crop(photo, hold.X, hold.Y, hold.Radius).Bytes;
    }

    /// <summary>The new photo at an old hold's spot: where the matcher puts it, or its old coordinates without the match.</summary>
    private async Task<byte[]?> SpotCropAsync(BlocwerkDbContext db, Guid wallId, Guid oldHoldId)
    {
        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == oldHoldId);
        if (hold is null)
        {
            return null;
        }

        var stagedGen = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId).Select(w => w.CurrentGeneration + 1).FirstAsync();
        var position = hold.WallPanelId is { } panelId
            ? await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId).Select(p => new { p.Col, p.Row }).FirstOrDefaultAsync()
            : null;
        var (col, row) = position is null ? (0, 0) : (position.Col, position.Row);
        var photo = await db.WallPanels.AsNoTracking()
            .Where(p => p.WallId == wallId && p.Generation == stagedGen && p.Col == col && p.Row == row)
            .Select(p => p.StagedPhoto)
            .FirstOrDefaultAsync();
        var matched = matchCache is null ? null : await matchCache.PeekAsync(db, wallId);
        var spot = matched?.CarriedWarpPositions?.TryGetValue(oldHoldId, out var at) == true ? at : null;
        return RefreshCheckCrops.Crop(photo, spot?.X ?? hold.X, spot?.Y ?? hold.Y, hold.Radius).Bytes;
    }
}
