// <copyright file="HoldColorProposer.Photos.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Holds.Colors;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The photo each hold is read from: the one its position is a fraction of. That is the live photo of its panel, which
/// a panel crop rewrites together with the hold positions. A hold without a panel is an old centre hold and sits on
/// the live centre panel's photo; <see cref="Wall.Photo"/> only stands in on a wall that has no centre panel. The holds
/// of known colour on the same photo are the palette a warm hold is compared with (<see cref="HoldColorPalette"/>).
/// </summary>
public sealed partial class HoldColorProposer
{
    /// <summary>Estimates every hold on its photo, off the caller's thread (decoding and reading pixels is CPU work).</summary>
    private static async Task<Dictionary<Guid, HoldColorEstimate>> EstimateAsync(
        BlocwerkDbContext db, Guid wallId, List<Hold> holds, List<Hold> live, CancellationToken ct)
    {
        var estimates = new Dictionary<Guid, HoldColorEstimate>();
        foreach (var onPhoto in holds.GroupBy(h => h.WallPanelId))
        {
            var photo = onPhoto.Key is { } panelId
                ? await LoadPanelPhotoAsync(db, panelId, ct)
                : await LoadCentrePhotoAsync(db, wallId, ct);
            var onThisPhoto = onPhoto.ToList();
            var known = live.Where(h => h.WallPanelId == onPhoto.Key && !h.HasProposedColor && !string.IsNullOrWhiteSpace(h.Color)).ToList();
            foreach (var (holdId, estimate) in await Task.Run(() => EstimateOnPhoto(photo, onThisPhoto, known), ct))
            {
                estimates[holdId] = estimate;
            }
        }

        return estimates;
    }

    private static List<(Guid HoldId, HoldColorEstimate Estimate)> EstimateOnPhoto(byte[]? photo, List<Hold> holds, List<Hold> known)
    {
        using var pixels = HoldPhotoPixels.TryOpen(photo);
        if (pixels is null)
        {
            return [.. holds.Select(h => (h.Id, HoldColorEstimate.Unset(HoldColorVerdict.NoPhoto)))];
        }

        // Read only when a hold needs it: most holds are settled by the estimate alone.
        var palette = new Lazy<HoldColorPalette>(() => new HoldColorPalette(
            known.Select(h => (Signature: HoldColorSampler.Read(h, pixels.Width, pixels.Height, pixels.At).Signature, Color: h.Color!))
                .Where(r => r.Signature is not null)
                .Select(r => (r.Signature!, r.Color))));
        return [.. holds.Select(h =>
        {
            var (estimate, signature) = HoldColorSampler.Read(h, pixels.Width, pixels.Height, pixels.At);
            return (h.Id, estimate.Color is null && signature is not null ? palette.Value.Refine(estimate, signature) : estimate);
        })];
    }

    private static Task<byte[]?> LoadPanelPhotoAsync(BlocwerkDbContext db, Guid panelId, CancellationToken ct) =>
        db.WallPanels.AsNoTracking().Where(p => p.Id == panelId).Select(p => p.Photo).FirstOrDefaultAsync(ct);

    private static async Task<byte[]?> LoadCentrePhotoAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var live = await LiveWallHolds.LoadPanelIdsAsync(db, wallId, ct);
        var centre = await db.WallPanels.AsNoTracking()
            .Where(p => live.Contains(p.Id) && p.Col == 0 && p.Row == 0)
            .Select(p => p.Photo)
            .FirstOrDefaultAsync(ct);
        return centre
            ?? await db.Walls.IgnoreQueryFilters().AsNoTracking().Where(w => w.Id == wallId).Select(w => w.Photo).FirstOrDefaultAsync(ct);
    }
}
