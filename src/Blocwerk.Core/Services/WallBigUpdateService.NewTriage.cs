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
    private async Task<NewHoldTriageOutcome> SuggestNewDiscardsAsync(
        BlocwerkDbContext db,
        Wall wall,
        int stagedGen,
        IReadOnlyList<CarryoverProposal> carryover,
        IReadOnlyList<Hold> oldHolds,
        IReadOnlyDictionary<Guid, byte[]> oldPanelPhotosById,
        byte[]? oldCentrePhoto,
        IReadOnlyList<NeighbourOverlap> neighbours,
        bool use3DEvidence,
        RemovalInputs removals)
    {
        var result = new NewHoldTriageOutcome([], []);
        var model3D = use3DEvidence ? await LoadEvidence3DAsync(db, wall.Id, carryover, oldHolds) : null;
        var panels = (await db.WallPanels
                .Where(p => p.WallId == wall.Id && p.Generation == stagedGen && p.StagedPhoto != null)
                .Select(p => new StagedPanelRef(p.Id, p.Col, p.Row))
                .ToListAsync())
            .OrderBy(p => p is { Col: 0, Row: 0 } ? 0 : 1)
            .ToList();
        var context = new TriageContext(wall, stagedGen, carryover, oldHolds.ToDictionary(h => h.Id), oldPanelPhotosById, oldCentrePhoto, neighbours, model3D, removals);
        TriagedCentre? centre = null;
        foreach (var panel in panels)
        {
            try
            {
                centre = await TriageOnePanelAsync(db, context, panel, centre, result) ?? centre;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "New-hold triage failed for panel {PanelId}; its detections stay kept by default", panel.Id);
            }
        }

        return result;
    }

    /// <summary>Triages one staged panel into <paramref name="result"/>; returns the triaged centre when this is the centre.</summary>
    private async Task<TriagedCentre?> TriageOnePanelAsync(
        BlocwerkDbContext db, TriageContext context, StagedPanelRef panel, TriagedCentre? centre, NewHoldTriageOutcome result)
    {
        var staged = await db.Holds
            .Where(h => h.WallPanelId == panel.Id && h.Generation == context.StagedGen)
            .ToListAsync();
        var twins = Twins(context.Carryover, context.OldById, staged);
        var isCentre = panel is { Col: 0, Row: 0 };
        var removable = RemovableAt(context.Removals, panel.Col, panel.Row);
        var oldPhoto = isCentre
            ? context.OldCentrePhoto
            : twins.Select(t => t.Old.WallPanelId).Concat(removable.Select(h => h.WallPanelId)).OfType<Guid>()
                .Select(context.OldPanelPhotosById.GetValueOrDefault).FirstOrDefault(p => p is not null);
        var overlap = isCentre ? null : context.Neighbours.FirstOrDefault(n => n.PanelId == panel.Id);
        var unpaired = Unpaired(staged, twins);
        var evidence = await PanelEvidenceAsync(db, context.Model3D, panel.Id, twins, unpaired.Count + removable.Count > 0);
        var verdicts = Verdicts3D(evidence, unpaired);
        var notFound = removable.Select(h => new WarpedOldHold(
            context.Removals.Warp[h.Id], h.Radius, context.Removals.WarpShapes?.GetValueOrDefault(h.Id), Carries(h, context.Removals), h.Id, Lab(h))).ToList();
        var oldPanelId = twins.Select(t => t.Old.WallPanelId).Concat(removable.Select(h => h.WallPanelId)).FirstOrDefault(id => id is not null);
        var outcome = await TriagePanelAsync(
            db, new TriagedPanel(panel.Id, staged, twins, oldPhoto, oldPanelId), OwnerSource(centre, overlap, staged), verdicts, notFound);
        Collect(result, outcome, verdicts);
        result.Swaps.AddRange((outcome?.Swaps ?? []).Select(w => (w, panel.Id)));
        result.Evidence3DModelId ??= verdicts is null ? null : context.Model3D?.Id;
        if (evidence is not null && context.Model3D is { } model)
        {
            CollectConflicts(result, panel.Id, model.Id, evidence, unpaired, outcome, verdicts);
            await CheckRemovalsAsync(db, model, evidence, new RemovalScope(panel.Id, staged, twins, removable, oldPhoto), context.Removals.Warp, result);
        }

        return isCentre && outcome is { } o ? new TriagedCentre(staged.ToDictionary(h => h.Id), o.Size, o.KeptNew, Settled(staged, unpaired, o)) : null;
    }

    /// <summary>The staged holds of a panel that are not its kept new holds: carried, hand-placed or discarded by its triage.</summary>
    private static List<TriageSpot> Settled(IReadOnlyList<Hold> staged, IReadOnlyList<Hold> unpaired, PanelTriage triage)
    {
        var keptNew = unpaired.Where(h => !triage.Discards.ContainsKey(h.Id)).Select(h => h.Id).ToHashSet();
        return staged.Where(h => !h.IsVirtual && !keptNew.Contains(h.Id)).Select(h => Spot(h.X, h.Y, h.Radius, triage.Size)).ToList();
    }

    /// <summary>A normalized spot in RAW pixels (radii are normalized to the longer side).</summary>
    private static TriageSpot Spot(double x, double y, double radius, (int Width, int Height) size) =>
        new(x * size.Width, y * size.Height, radius * Math.Max(size.Width, size.Height));

    private static List<Hold> Unpaired(IReadOnlyList<Hold> staged, IReadOnlyList<(Hold Old, Hold New)> twins)
    {
        var paired = twins.Select(t => t.New.Id).ToHashSet();
        return staged.Where(h => h.IsAutoDetected && !h.IsVirtual && !paired.Contains(h.Id)).ToList();
    }

    private static void Collect(NewHoldTriageOutcome result, PanelTriage? outcome, IReadOnlyDictionary<Guid, Evidence3DVerdict>? verdicts)
    {
        foreach (var (id, reason) in outcome?.Discards ?? [])
        {
            result.Discards[id] = reason;
        }

        result.SeenIn3D.AddRange((verdicts ?? new Dictionary<Guid, Evidence3DVerdict>())
            .Where(v => v.Value == Evidence3DVerdict.SeenIn3D && !result.Discards.ContainsKey(v.Key)).Select(v => v.Key));
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
        TriagedPanel panel,
        Func<(int Width, int Height), OverlapOwner?> owner,
        IReadOnlyDictionary<Guid, Evidence3DVerdict>? verdicts,
        IReadOnlyList<WarpedOldHold> notFound)
    {
        var newPhoto = await db.WallPanels.Where(p => p.Id == panel.PanelId).Select(p => p.StagedPhoto).FirstAsync();
        if (newPhoto is null || OverlapSeedLoader.RawSize(newPhoto) is not { } newSize)
        {
            return null;
        }

        var oldSize = panel.OldPhoto is null ? null : OverlapSeedLoader.RawSize(panel.OldPhoto);
        var candidates = Unpaired(panel.Staged, panel.Twins).Select(h => Candidate(h, newSize)).ToList();
        var pairs = oldSize is not { } os
            ? []
            : panel.Twins.Select(t => new PointPair(
                t.New.X * newSize.Width, t.New.Y * newSize.Height, t.Old.X * os.Width, t.Old.Y * os.Height)).ToList();
        var markers = await MarkerQuadsAsync(db, panel.PanelId, staged: true, newSize);
        markers.AddRange(await RescuedMarkerQuadsAsync(newPhoto));
        var oldMarkers = panel.OldPanelId is { } oldPanelId && oldSize is { } size ? await MarkerQuadsAsync(db, oldPanelId, staged: false, size) : null;
        var input = new NewHoldTriageInput(
            candidates, pairs, oldSize, markers, owner(newSize), verdicts,
            panel.Twins.Select(t => Spot(t.New.X, t.New.Y, t.New.Radius, newSize)).ToList(),
            notFound.Select(n => Spot(n.At.X, n.At.Y, n.Radius, newSize)).ToList(),
            CarriedOutlines(panel.Twins, notFound, newSize),
            oldMarkers,
            ColourShift(panel.Twins));
        Func<IReadOnlyList<PresenceQuery>, IReadOnlyList<double?>>? presence =
            presenceProbe is null || panel.OldPhoto is null ? null : q => presenceProbe.Score(panel.OldPhoto, newPhoto, q);
        var swaps = new List<TriageSwap>();
        var result = NewHoldTriage.Classify(input, presence, swaps);
        logger.LogInformation(
            "New-hold triage on panel {PanelId}: {Discarded} of {Candidates} unpaired detections discarded by default ({Reasons})",
            panel.PanelId, result.Count, candidates.Count,
            string.Join(", ", result.GroupBy(r => r.Value).Select(g => $"{g.Key} {g.Count()}")));
        var kept = candidates.Where(c => !result.ContainsKey(c.Id)).Select(c => (c.X, c.Y)).ToList();
        return new PanelTriage(result, newSize, kept, swaps);
    }
}
