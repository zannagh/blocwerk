// <copyright file="WallBigUpdateService.NewTriage.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The default verdict on each staged panel's unpaired detections: which ones the review should discard
/// unless the user keeps them (see <see cref="NewHoldTriage"/>). A suggestion only — never applied here.
/// The centre goes first, so a neighbour's triage knows which of the centre's detections are new holds.
/// </summary>
public partial class WallBigUpdateService
{
    private async Task<Dictionary<Guid, NewHoldDiscardReason>> SuggestNewDiscardsAsync(
        BlocwerkDbContext db,
        Wall wall,
        int stagedGen,
        IReadOnlyList<CarryoverProposal> carryover,
        IReadOnlyList<Hold> oldHolds,
        IReadOnlyDictionary<Guid, byte[]> oldPanelPhotosById,
        IReadOnlyList<NeighbourOverlap> neighbours)
    {
        var result = new Dictionary<Guid, NewHoldDiscardReason>();
        var panels = (await db.WallPanels
                .Where(p => p.WallId == wall.Id && p.Generation == stagedGen && p.StagedPhoto != null)
                .Select(p => new { p.Id, p.Col, p.Row })
                .ToListAsync())
            .OrderBy(p => p is { Col: 0, Row: 0 } ? 0 : 1)
            .ToList();
        var oldById = oldHolds.ToDictionary(h => h.Id);
        TriagedCentre? centre = null;
        foreach (var panel in panels)
        {
            try
            {
                var staged = await db.Holds
                    .Where(h => h.WallPanelId == panel.Id && h.Generation == stagedGen)
                    .ToListAsync();
                var twins = Twins(carryover, oldById, staged);
                var isCentre = panel is { Col: 0, Row: 0 };
                var oldPhoto = isCentre
                    ? wall.Photo
                    : twins.Select(t => t.Old.WallPanelId).OfType<Guid>().Select(oldPanelPhotosById.GetValueOrDefault).FirstOrDefault();
                var overlap = isCentre ? null : neighbours.FirstOrDefault(n => n.PanelId == panel.Id);
                var outcome = await TriagePanelAsync(db, panel.Id, staged, twins, oldPhoto, OwnerSource(centre, overlap, staged));
                foreach (var (id, reason) in outcome?.Discards ?? [])
                {
                    result[id] = reason;
                }

                if (isCentre && outcome is { } o)
                {
                    centre = new TriagedCentre(staged.ToDictionary(h => h.Id), o.Size, o.KeptNew);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "New-hold triage failed for panel {PanelId}; its detections stay kept by default", panel.Id);
            }
        }

        return result;
    }

    private static List<(Hold Old, Hold New)> Twins(
        IReadOnlyList<CarryoverProposal> carryover, IReadOnlyDictionary<Guid, Hold> oldById, IReadOnlyList<Hold> staged)
    {
        var stagedById = staged.ToDictionary(h => h.Id);
        return carryover
            .Where(p => oldById.ContainsKey(p.OldHoldId) && stagedById.ContainsKey(p.NewHoldId))
            .Select(p => (oldById[p.OldHoldId], stagedById[p.NewHoldId]))
            .ToList();
    }

    private async Task<PanelTriage?> TriagePanelAsync(
        BlocwerkDbContext db,
        Guid panelId,
        IReadOnlyList<Hold> staged,
        IReadOnlyList<(Hold Old, Hold New)> twins,
        byte[]? oldPhoto,
        Func<(int Width, int Height), OverlapOwner?> owner)
    {
        var newPhoto = await db.WallPanels.Where(p => p.Id == panelId).Select(p => p.StagedPhoto).FirstAsync();
        if (newPhoto is null || OverlapSeedLoader.RawSize(newPhoto) is not { } newSize)
        {
            return null;
        }

        var oldSize = oldPhoto is null ? null : OverlapSeedLoader.RawSize(oldPhoto);
        var paired = twins.Select(t => t.New.Id).ToHashSet();
        var candidates = staged
            .Where(h => h.IsAutoDetected && !h.IsVirtual && !paired.Contains(h.Id))
            .Select(h => new TriageCandidate(h.Id, h.X * newSize.Width, h.Y * newSize.Height))
            .ToList();
        var pairs = oldSize is not { } os
            ? []
            : twins.Select(t => new PointPair(
                t.New.X * newSize.Width, t.New.Y * newSize.Height, t.Old.X * os.Width, t.Old.Y * os.Height)).ToList();
        var markers = await StagedMarkerQuadsAsync(db, panelId, newSize);
        markers.AddRange(await RescuedMarkerQuadsAsync(newPhoto));
        var input = new NewHoldTriageInput(candidates, pairs, oldSize, markers, owner(newSize));
        Func<IReadOnlyList<PresenceQuery>, IReadOnlyList<double?>>? presence =
            presenceProbe is null || oldPhoto is null ? null : q => presenceProbe.Score(oldPhoto, newPhoto, q);
        var result = NewHoldTriage.Classify(input, presence);
        logger.LogInformation(
            "New-hold triage on panel {PanelId}: {Discarded} of {Candidates} unpaired detections discarded by default ({Reasons})",
            panelId, result.Count, candidates.Count,
            string.Join(", ", result.GroupBy(r => r.Value).Select(g => $"{g.Key} {g.Count()}")));
        var kept = candidates.Where(c => !result.ContainsKey(c.Id)).Select(c => (c.X, c.Y)).ToList();
        return new PanelTriage(result, newSize, kept);
    }
}
