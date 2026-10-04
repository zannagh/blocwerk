// <copyright file="PanelCropService.Remap.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services.PanelCrop;

/// <summary>
/// The re-mapping half of <see cref="PanelCropService"/>: everything stored in the panel photo's normalized frame moves
/// to the new frame in the SAME SaveChanges as the photo. That is the panel's holds (every generation on this row, so a
/// historic boulder drawn on it still lines up), its live marker observations (the ArUco corners the overlap seeds, plane
/// mapping and 3D registration anchor on) and its pending hold proposals. Hold links are id pairs and need no mapping; the
/// overlap homographies between panels are not stored, they are fitted from these coordinates on demand, so they follow.
/// </summary>
public sealed partial class PanelCropService
{
    /// <summary>
    /// Works out a crop of the photo as it is now: the rectangle relative to the original (snapped to its pixels), the
    /// frame map, and which LIVE holds it cuts off. Older-generation holds on the row are re-mapped but never removed.
    /// </summary>
    private static async Task<PanelCropPlan> PlanCropAsync(BlocwerkDbContext db, CropTarget target, PanelCropRect rect, CancellationToken ct)
    {
        var source = target.Crop?.OriginalPhoto ?? target.Panel.Photo!;
        var originalRect = PanelCropImage.Snapped(source, rect.Within(target.Current));
        var map = PanelFrameMap.OutOfCrop(target.Current).Then(PanelFrameMap.IntoCrop(originalRect));

        var live = await db.Holds.AsNoTracking()
            .Where(h => h.WallPanelId == target.Panel.Id && h.Generation == target.Panel.Generation)
            .ToListAsync(ct);
        var removed = live.Where(h => PanelCropCutoff.IsCutOff(map.Mapped(h))).Select(h => h.Id).ToList();
        var boulders = await AffectedBouldersAsync(db, removed, ct);
        return new PanelCropPlan(originalRect, map, new PanelCropPreview(removed, live.Count - removed.Count, boulders));
    }

    /// <summary>The boulders that use any of <paramref name="holdIds"/>, active ones first.</summary>
    private static async Task<List<PanelCropAffectedBoulder>> AffectedBouldersAsync(
        BlocwerkDbContext db, List<Guid> holdIds, CancellationToken ct)
    {
        if (holdIds.Count == 0)
        {
            return [];
        }

        var rows = await db.BoulderHolds.AsNoTracking()
            .Where(bh => holdIds.Contains(bh.HoldId))
            .Select(bh => new { bh.Boulder.Id, bh.Boulder.Name, bh.Boulder.Grade, bh.Boulder.IsArchived, bh.Boulder.IsHistoric })
            .Distinct()
            .ToListAsync(ct);
        return rows
            .Select(b => new PanelCropAffectedBoulder(b.Id, b.Name, b.Grade, !b.IsArchived && !b.IsHistoric))
            .OrderByDescending(b => b.IsActive).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Re-maps the panel's frame-bound rows through <paramref name="map"/>, removes <paramref name="removedHoldIds"/> by
    /// the ordinary hold-removal rules, commits it all with the photo change in one journalled save, then queues the
    /// panel's live holds so the 3D placement follows. Returns how many boulders the removal made historic.
    /// </summary>
    private async Task<int> ApplyAsync(
        BlocwerkDbContext db, CropTarget target, PanelFrameMap map, IReadOnlyList<Guid> removedHoldIds, string label, CancellationToken ct)
    {
        var holds = await db.Holds.Where(h => h.WallPanelId == target.Panel.Id).ToListAsync(ct);
        var removed = removedHoldIds.ToHashSet();
        var historic = await HoldDeletion.PrepareHoldsForDeleteAsync(db, removed, HoldDeleteBoulderPolicy.DetachAndFlagHistoric, ct: ct);
        foreach (var hold in holds)
        {
            if (removed.Contains(hold.Id))
            {
                db.Holds.Remove(hold);
            }
            else
            {
                map.Apply(hold);
            }
        }

        await RemapMarkersAsync(db, target.Panel.Id, map, ct);
        await RemapProposalsAsync(db, target.Panel.Id, map, ct);
        using (changeJournal?.BeginBatch(label, ChangeJournalScopeKind.Wall, target.Panel.WallId))
        {
            await db.SaveChangesAsync(ct);
        }

        var kept = holds.Where(h => !removed.Contains(h.Id) && h.Generation == target.Panel.Generation).Select(h => h.Id).ToList();
        refinementQueue?.Enqueue(target.Panel.WallId, kept);
        return historic;
    }

    /// <summary>The live photo's marker corners (normalized, same frame as the holds) moved with the frame.</summary>
    private static async Task RemapMarkersAsync(BlocwerkDbContext db, Guid panelId, PanelFrameMap map, CancellationToken ct)
    {
        var markers = await db.WallMarkerObservations
            .Where(o => o.WallPanelId == panelId && !o.FromStagedPhoto)
            .ToListAsync(ct);
        foreach (var marker in markers)
        {
            var corners = JsonSerializer.Deserialize<double[][]>(marker.CornersJson) ?? [];
            var mapped = corners
                .Where(c => c.Length >= 2)
                .Select(c => map.Point(c[0], c[1]))
                .Select(p => new[] { p.X, p.Y });
            marker.CornersJson = JsonSerializer.Serialize(mapped);
        }
    }

    /// <summary>Pending hold proposals pointing into this panel photo moved with the frame.</summary>
    private static async Task RemapProposalsAsync(BlocwerkDbContext db, Guid panelId, PanelFrameMap map, CancellationToken ct)
    {
        var proposals = await db.HoldProposals
            .Where(p => p.PanelId == panelId && p.Status == HoldProposalStatus.Pending && p.PanelX != null && p.PanelY != null)
            .ToListAsync(ct);
        foreach (var proposal in proposals)
        {
            (proposal.PanelX, proposal.PanelY) = map.Point(proposal.PanelX!.Value, proposal.PanelY!.Value);
            proposal.PanelRadius *= Math.Sqrt(map.ScaleX * map.ScaleY);
        }
    }
}
