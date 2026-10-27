// <copyright file="UpdateReviewService.Crops.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh.Review;

/// <summary>
/// The pictures of a review item, made on request and kept in a small cache (what they show is fixed while the update
/// is open). A card's pictures are the confirm screen's. An old hold: "old" around it on its live photo, "new" at the
/// spot the matcher puts it on the new photo; while the match is not at hand (after a restart, until the summary is made
/// again) there is no "new" picture at all, and nothing is cached: the old coordinates on the new photo can show bare
/// wall a few centimetres off and invite a wrong "gone". A hold that may have moved: "old" as above, "new" at the detection
/// it is paired with. A detection: "new" only; an old picture at the same coordinates
/// could show another place, so there is none. Every lookup is scoped to the run's wall and session.
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

            var (bytes, remember) = await MakeCropAsync(db, refresh.WallId, sessionId, prefix, id, view);
            if (remember)
            {
                if (Crops.Count >= MaxCached)
                {
                    Crops.Clear();
                }

                Crops[(sessionId, key, view)] = bytes;
            }

            return bytes;
        }
    }

    private async Task<(byte[]? Bytes, bool Remember)> MakeCropAsync(
        BlocwerkDbContext db, Guid wallId, Guid sessionId, char prefix, Guid id, CheckCropView view)
    {
        if (prefix == 'r')
        {
            var suggestion = await db.WallUpdateRelocationProposals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.SessionId == sessionId);
            return suggestion is null ? (null, true)
                : (view == CheckCropView.Old ? await OldCropAsync(db, wallId, suggestion.OldHoldId) : await StagedCropAsync(db, wallId, suggestion.NewHoldId), true);
        }

        if (prefix == 'm')
        {
            return view switch
            {
                CheckCropView.Old => (await OldCropAsync(db, wallId, id), true),
                CheckCropView.New => (await TwinCropAsync(db, wallId, sessionId, id), false),
                _ => (null, true),
            };
        }

        if (prefix is 'n' or 'd')
        {
            return (view == CheckCropView.New ? await StagedCropAsync(db, wallId, id) : null, true);
        }

        return view switch
        {
            CheckCropView.Old => (await OldCropAsync(db, wallId, id), true),
            CheckCropView.New => await SpotCropAsync(db, wallId, id),
            _ => (null, true),
        };
    }

    private static async Task<byte[]?> OldCropAsync(BlocwerkDbContext db, Guid wallId, Guid holdId)
    {
        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId && h.WallId == wallId);
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

    private static async Task<byte[]?> StagedCropAsync(BlocwerkDbContext db, Guid wallId, Guid holdId)
    {
        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId && h.WallId == wallId);
        if (hold is not { WallPanelId: { } panelId })
        {
            return null;
        }

        var photo = await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId).Select(p => p.StagedPhoto).FirstOrDefaultAsync();
        return RefreshCheckCrops.Crop(photo, hold.X, hold.Y, hold.Radius).Bytes;
    }

    /// <summary>
    /// The detection a carried old hold is paired with, on the new photo (a hold that may have moved). Not remembered: the
    /// pairing is the person's to change.
    /// </summary>
    private static async Task<byte[]?> TwinCropAsync(BlocwerkDbContext db, Guid wallId, Guid sessionId, Guid oldHoldId)
    {
        var twin = await db.WallUpdateHoldDecisions.AsNoTracking()
            .Where(d => d.SessionId == sessionId && d.Kind == WallUpdateHoldDecisionKind.Carry && d.HoldId == oldHoldId)
            .Select(d => d.PairedHoldId)
            .FirstOrDefaultAsync();
        return twin is { } id ? await StagedCropAsync(db, wallId, id) : null;
    }

    /// <summary>The new photo at an old hold's spot, where the matcher puts it; none (and not remembered) without the match.</summary>
    private async Task<(byte[]? Bytes, bool Remember)> SpotCropAsync(BlocwerkDbContext db, Guid wallId, Guid oldHoldId)
    {
        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == oldHoldId && h.WallId == wallId);
        var matched = hold is null || matchCache is null ? null : await matchCache.PeekAsync(db, wallId);
        if (hold is null || matched?.CarriedWarpPositions?.TryGetValue(oldHoldId, out var spot) != true || spot is null)
        {
            return (null, hold is null);
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
        return (RefreshCheckCrops.Crop(photo, spot.X, spot.Y, hold.Radius).Bytes, true);
    }
}
