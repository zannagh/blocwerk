// <copyright file="WallBigUpdateService.Looks.cs" company="Blocwerk">
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
/// The look check of a matched session (<see cref="HoldLookCheck"/>) over every re-photographed panel that was aligned:
/// a pair whose detection shows another hold is taken apart again (the old hold counts as not found, the detection as
/// new), an unclear pair is reported, and every old hold without a twin gets a score at its predicted spot. One verdict
/// per physical hold: holds linked across panels take the least drastic one. Recomputed with the session on every
/// resume; the only thing read back is a replacement already recorded as a card, which stays one. Without a presence
/// probe nothing is judged, and a score that cannot be measured is no verdict.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// Judges the session's pairs. Replaced pairs leave <paramref name="carryover"/>; a freed centre detection joins
    /// <paramref name="newCenter"/>. Call after the neighbours were matched and before the centre's hand-placed merge.
    /// </summary>
    private async Task<LookOutcome> JudgeLooksAsync(
        BlocwerkDbContext db,
        Wall wall,
        int stagedGen,
        LookInputs inputs,
        List<CarryoverProposal> carryover,
        IReadOnlyList<Guid> notFound,
        IReadOnlyDictionary<Guid, HoldPositionNorm> warp,
        List<Guid> newCenter)
    {
        var looks = new LookOutcome();
        if (presenceProbe is null)
        {
            return looks;
        }

        foreach (var panel in await LoadLookPanelsAsync(db, wall.Id, stagedGen, inputs))
        {
            await ScorePanelAsync(looks, panel, carryover, notFound, warp);
        }

        if (looks.Pairs.Count + looks.Spots.Count == 0)
        {
            return looks;
        }

        var links = await db.HoldLinks.AsNoTracking()
            .Where(l => l.WallId == wall.Id && l.Kind == HoldLinkKind.Same)
            .Select(l => new { l.HoldAId, l.HoldBId })
            .ToListAsync();
        looks.Components = HoldLookCheck.Components(links.Select(l => (l.HoldAId, l.HoldBId)));
        SettleVerdicts(looks, await RecordedReplacementsAsync(db, wall.Id));
        ApplyVerdicts(looks, carryover, newCenter);
        LogLooks(looks);
        return looks;
    }

    /// <summary>The aligned staged panels with their old holds and both photos, centre first.</summary>
    private static async Task<List<LookPanel>> LoadLookPanelsAsync(BlocwerkDbContext db, Guid wallId, int stagedGen, LookInputs inputs)
    {
        var staged = await db.WallPanels.AsNoTracking()
            .Where(p => p.WallId == wallId && p.Generation == stagedGen && p.StagedPhoto != null)
            .OrderBy(p => p.Row == 0 && p.Col == 0 ? 0 : 1).ThenBy(p => p.Row).ThenBy(p => p.Col)
            .Select(p => new { p.Id, p.Col, p.Row, p.StagedPhoto })
            .ToListAsync();
        var panels = new List<LookPanel>();
        foreach (var p in staged)
        {
            var old = inputs.OldByPosition.GetValueOrDefault((p.Col, p.Row));
            var oldPhoto = p is { Col: 0, Row: 0 }
                ? inputs.OldCentrePhoto
                : old is { Count: > 0 } && old[0].WallPanelId is { } id ? inputs.OldPanelPhotosById.GetValueOrDefault(id) : null;
            if (old is not { Count: > 0 } || oldPhoto is null || inputs.UnalignedPanelIds.Contains(p.Id))
            {
                continue;
            }

            var holds = await db.Holds.AsNoTracking().Where(h => h.WallPanelId == p.Id && h.Generation == stagedGen).OrderBy(h => h.Id).ToListAsync();
            panels.Add(new LookPanel(p.Id, p.Col, p.Row, old, holds, oldPhoto, p.StagedPhoto!));
        }

        return panels;
    }

    /// <summary>Reports the pairs that are not clearly the same and takes the replaced and mispaired ones apart.</summary>
    private static void ApplyVerdicts(LookOutcome looks, List<CarryoverProposal> carryover, List<Guid> newCenter)
    {
        foreach (var pair in looks.Pairs.Where(p => p.Look is HoldLook.Unsure or HoldLook.Replaced))
        {
            if (pair.Look == HoldLook.Unsure || !pair.Mispaired)
            {
                looks.Findings.Add(new HoldLookFinding(pair.Pair.OldHoldId, pair.Pair.NewHoldId, pair.Panel.PanelId, pair.Look!.Value, pair.Score));
            }

            if (pair.Look == HoldLook.Replaced)
            {
                carryover.Remove(pair.Pair);
                if (pair.Panel is { Col: 0, Row: 0 })
                {
                    newCenter.Add(pair.Pair.NewHoldId);
                }
            }
        }
    }

    /// <summary>
    /// Scores one panel's pairs and the predicted spots of its old holds without a twin. A pair that scores as replaced
    /// is asked two more things: whether the detection's spot looks unchanged since the old photo (then the pair is not a
    /// replacement but a mismatch), and how the old hold's own predicted spot looks (what it is judged by in that case).
    /// </summary>
    private async Task ScorePanelAsync(
        LookOutcome looks, LookPanel panel, List<CarryoverProposal> carryover, IReadOnlyList<Guid> notFound, IReadOnlyDictionary<Guid, HoldPositionNorm> warp)
    {
        if (OverlapSeedLoader.RawSize(panel.NewPhoto) is not { } newSize || OverlapSeedLoader.RawSize(panel.OldPhoto) is not { } oldSize)
        {
            return;
        }

        // A virtual hold has no pixels of its own: its look says nothing.
        var oldById = panel.OldHolds.Where(h => !h.IsVirtual).ToDictionary(h => h.Id);
        var stagedById = panel.Staged.ToDictionary(h => h.Id);
        var pairs = carryover.Where(p => oldById.ContainsKey(p.OldHoldId) && stagedById.ContainsKey(p.NewHoldId)).ToList();
        var newToOld = pairs
            .Select(p => new PointPair(
                stagedById[p.NewHoldId].X * newSize.Width, stagedById[p.NewHoldId].Y * newSize.Height,
                oldById[p.OldHoldId].X * oldSize.Width, oldById[p.OldHoldId].Y * oldSize.Height))
            .ToList();
        var frame = new LookFrame(panel, oldById, stagedById, newSize, oldSize, warp, newToOld);
        var missing = notFound.Where(id => oldById.ContainsKey(id) && warp.ContainsKey(id)).Distinct().ToList();
        var scores = await ScoreLooksAsync(panel, pairs.Select(frame.PairQuery).Concat(missing.Select(frame.SpotQuery)).ToList());
        var judged = pairs.Select((p, i) => new JudgedPair(
            p, panel, scores[i], frame.RadiusPx(oldById[p.OldHoldId]), frame.RadiusPx(stagedById[p.NewHoldId]),
            HoldLookCheck.SameKind(oldById[p.OldHoldId].FingerprintJson, stagedById[p.NewHoldId].FingerprintJson, moveOptions))).ToList();
        looks.Pairs.AddRange(judged);
        looks.Spots.AddRange(missing.Select((id, i) => new JudgedSpot(id, panel, scores[pairs.Count + i], frame.RadiusPx(oldById[id]))));

        bool LowAt(int i) => scores[i] is { } s && HoldLookCheck.Judge(s, moveOptions) == HoldLook.Replaced;
        var low = Enumerable.Range(0, pairs.Count).Where(LowAt).ToList();
        if (low.Count == 0)
        {
            return;
        }

        var others = newToOld.Where((_, i) => !LowAt(i)).ToList();
        var own = low.Where(i => warp.ContainsKey(pairs[i].OldHoldId)).ToList();
        var more = await ScoreLooksAsync(
            panel, low.Select(i => frame.UnchangedQuery(pairs[i], others)).Concat(own.Select(i => frame.SpotQuery(pairs[i].OldHoldId))).ToList());
        for (var k = 0; k < low.Count; k++)
        {
            judged[low[k]].Mispaired = more[k] >= NewHoldTriage.SameAsOldScore;
        }

        looks.Spots.AddRange(own.Select((i, k) => new JudgedSpot(pairs[i].OldHoldId, panel, more[low.Count + k], judged[i].OldRadiusPx)));
    }

    /// <summary>
    /// One score per query, off the request thread; null where there was no query or the probe could not measure, and
    /// all null when the probe failed: a broken probe must neither take pairs apart nor mark boulders.
    /// </summary>
    private async Task<double?[]> ScoreLooksAsync(LookPanel panel, IReadOnlyList<PresenceQuery?> queries)
    {
        var asked = Enumerable.Range(0, queries.Count).Where(i => queries[i] is not null).ToList();
        var scores = new double?[queries.Count];
        if (asked.Count == 0 || presenceProbe is not { } probe)
        {
            return scores;
        }

        try
        {
            var measured = await Task.Run(() => probe.Score(panel.OldPhoto, panel.NewPhoto, asked.Select(i => queries[i]!.Value).ToList()));
            for (var k = 0; k < asked.Count && k < measured.Count; k++)
            {
                scores[asked[k]] = measured[k];
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Look check failed on panel {PanelId}; its pairs stay as matched and no boulder is marked for it", panel.PanelId);
            Array.Clear(scores);
        }

        return scores;
    }

    /// <summary>The pairs this open session already asked about as replaced: they stay replaced, whatever a later run measures.</summary>
    private static async Task<HashSet<(Guid Old, Guid New)>> RecordedReplacementsAsync(BlocwerkDbContext db, Guid wallId)
    {
        var open = await WallUpdateSessions.FindOpenAsync(db, wallId);
        if (open is null)
        {
            return [];
        }

        var cards = await db.WallUpdateExceptions.AsNoTracking()
            .Where(e => e.SessionId == open.Id && e.Kind == UpdateExceptionKind.ReplacedByOther && e.OldHoldId != null && e.StagedHoldId != null)
            .Select(e => new { Old = e.OldHoldId!.Value, New = e.StagedHoldId!.Value })
            .ToListAsync();
        return cards.Select(c => (c.Old, c.New)).ToHashSet();
    }

    /// <summary>
    /// Turns the scores into verdicts: by the bands (a pair whose fingerprints are alike is never replaced, it may only
    /// have been turned), then the least drastic verdict of each physical hold for all its panels, then the recorded
    /// replacements. Also notes which physical holds still show somewhere.
    /// </summary>
    private void SettleVerdicts(LookOutcome looks, HashSet<(Guid Old, Guid New)> recorded)
    {
        foreach (var pair in looks.Pairs.Where(p => p.Score is not null))
        {
            var look = HoldLookCheck.Judge(pair.Score!.Value, moveOptions);
            pair.Look = look == HoldLook.Replaced && pair.SameKind ? HoldLook.Unsure : look;
        }

        var mildest = looks.Pairs.Where(p => p.Look is not null)
            .GroupBy(p => looks.RootOf(p.Pair.OldHoldId))
            .ToDictionary(g => g.Key, g => g.Min(p => p.Look!.Value));
        foreach (var pair in looks.Pairs)
        {
            if (recorded.Contains((pair.Pair.OldHoldId, pair.Pair.NewHoldId)))
            {
                (pair.Look, pair.Mispaired) = (HoldLook.Replaced, false);
            }
            else if (pair.Look is not null)
            {
                pair.Look = mildest[looks.RootOf(pair.Pair.OldHoldId)];
            }
        }

        looks.Present.UnionWith(looks.Pairs.Where(p => p.Look is HoldLook.Same or HoldLook.Unsure).Select(p => looks.RootOf(p.Pair.OldHoldId)));
        looks.Present.UnionWith(looks.Spots
            .Where(s => s.Score is { } score && !HoldLookCheck.LooksGone(score, moveOptions))
            .Select(s => looks.RootOf(s.OldHoldId)));
    }

    /// <summary>
    /// Fills <see cref="LookOutcome.NotFoundAgain"/>: the replaced holds, and the holds without a twin whose predicted
    /// spot confidently no longer shows them. A hold whose spot is unclear or could not be measured (outside the new
    /// photo, an unaligned panel), or that a linked panel still shows, is left out. The old holds of the pairs taken
    /// apart join <paramref name="removedCandidates"/> here, after the hand-placed merge, so that merge cannot pair them
    /// with the same detection again.
    /// </summary>
    private void CollectNotFoundAgain(LookOutcome looks, List<Guid> removedCandidates)
    {
        foreach (var pair in looks.Pairs.Where(p => p.Look == HoldLook.Replaced))
        {
            removedCandidates.Add(pair.Pair.OldHoldId);
            if (!pair.Mispaired)
            {
                looks.NotFoundAgain[pair.Pair.OldHoldId] = HoldMoveOutcome.Replaced;
            }
        }

        foreach (var id in removedCandidates.Where(id => !looks.NotFoundAgain.ContainsKey(id) && looks.IsGone(id, moveOptions)).ToList())
        {
            looks.NotFoundAgain[id] = HoldMoveOutcome.NotFound;
        }
    }
}
