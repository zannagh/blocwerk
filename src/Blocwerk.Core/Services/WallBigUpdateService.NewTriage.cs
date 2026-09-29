// <copyright file="WallBigUpdateService.NewTriage.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
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
/// </summary>
public partial class WallBigUpdateService
{
    private async Task<Dictionary<Guid, NewHoldDiscardReason>> SuggestNewDiscardsAsync(
        BlocwerkDbContext db,
        Wall wall,
        int stagedGen,
        IReadOnlyList<CarryoverProposal> carryover,
        IReadOnlyList<Hold> oldHolds,
        IReadOnlyDictionary<Guid, byte[]> oldPanelPhotosById)
    {
        var result = new Dictionary<Guid, NewHoldDiscardReason>();
        var panels = await db.WallPanels
            .Where(p => p.WallId == wall.Id && p.Generation == stagedGen && p.StagedPhoto != null)
            .Select(p => new { p.Id, p.Col, p.Row })
            .ToListAsync();
        var oldById = oldHolds.ToDictionary(h => h.Id);
        foreach (var panel in panels)
        {
            try
            {
                var staged = await db.Holds
                    .Where(h => h.WallPanelId == panel.Id && h.Generation == stagedGen)
                    .ToListAsync();
                var twins = carryover
                    .Where(p => oldById.ContainsKey(p.OldHoldId))
                    .Select(p => (Old: oldById[p.OldHoldId], New: staged.FirstOrDefault(h => h.Id == p.NewHoldId)))
                    .Where(p => p.New is not null)
                    .Select(p => (p.Old, New: p.New!))
                    .ToList();
                var oldPhoto = panel is { Col: 0, Row: 0 }
                    ? wall.Photo
                    : twins.Select(t => t.Old.WallPanelId).OfType<Guid>().Select(oldPanelPhotosById.GetValueOrDefault).FirstOrDefault();
                foreach (var (id, reason) in await TriagePanelAsync(db, panel.Id, staged, twins, oldPhoto))
                {
                    result[id] = reason;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "New-hold triage failed for panel {PanelId}; its detections stay kept by default", panel.Id);
            }
        }

        return result;
    }

    private async Task<Dictionary<Guid, NewHoldDiscardReason>> TriagePanelAsync(
        BlocwerkDbContext db,
        Guid panelId,
        IReadOnlyList<Hold> staged,
        IReadOnlyList<(Hold Old, Hold New)> twins,
        byte[]? oldPhoto)
    {
        var newPhoto = await db.WallPanels.Where(p => p.Id == panelId).Select(p => p.StagedPhoto).FirstAsync();
        if (newPhoto is null || OverlapSeedLoader.RawSize(newPhoto) is not { } newSize)
        {
            return [];
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
        var input = new NewHoldTriageInput(candidates, pairs, oldSize, markers);
        Func<IReadOnlyList<PresenceQuery>, IReadOnlyList<double?>>? presence =
            presenceProbe is null || oldPhoto is null ? null : q => presenceProbe.Score(oldPhoto, newPhoto, q);
        var result = NewHoldTriage.Classify(input, presence);
        logger.LogInformation(
            "New-hold triage on panel {PanelId}: {Discarded} of {Candidates} unpaired detections discarded by default ({Reasons})",
            panelId, result.Count, candidates.Count,
            string.Join(", ", result.GroupBy(r => r.Value).Select(g => $"{g.Key} {g.Count()}")));
        return result;
    }

    private static async Task<List<IReadOnlyList<(double X, double Y)>>> StagedMarkerQuadsAsync(
        BlocwerkDbContext db, Guid panelId, (int Width, int Height) size)
    {
        var rows = await db.WallMarkerObservations
            .Where(o => o.WallPanelId == panelId && o.FromStagedPhoto && !o.Synthetic)
            .Select(o => o.CornersJson)
            .ToListAsync();
        var quads = new List<IReadOnlyList<(double X, double Y)>>();
        foreach (var json in rows)
        {
            try
            {
                if (JsonSerializer.Deserialize<double[][]>(json) is { Length: 4 } corners && corners.All(c => c.Length >= 2))
                {
                    quads.Add(corners.Select(c => (c[0] * size.Width, c[1] * size.Height)).ToList());
                }
            }
            catch (JsonException)
            {
                // An unreadable row is simply not a marker to avoid.
            }
        }

        return quads;
    }
}
