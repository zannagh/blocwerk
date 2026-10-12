// <copyright file="WallBigUpdateService.Looks.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The look check of a matched session (<see cref="HoldLookCheck"/>), per re-photographed panel that was aligned: a pair
/// whose detection shows another hold is taken apart again (the old hold counts as not found, the detection as new), an
/// unclear pair is reported, and every old hold without a twin gets a score at its predicted spot. Recomputed with the
/// session on every resume and never stored; without a presence probe or an old photo nothing is judged.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>The old photo of a neighbour panel's carried holds, or null when it is not loaded.</summary>
    private static byte[]? OldPanelPhoto(IReadOnlyList<Hold>? oldHolds, IReadOnlyDictionary<Guid, byte[]> oldPanelPhotosById) =>
        oldHolds is { Count: > 0 } && oldHolds[0].WallPanelId is { } id ? oldPanelPhotosById.GetValueOrDefault(id) : null;

    /// <summary>
    /// Judges one panel's pairs and probes its unpaired old holds. A replaced pair leaves <paramref name="carryover"/>;
    /// its detection joins <paramref name="unpairedNew"/> (the centre's new holds) when the caller tracks them.
    /// </summary>
    private void JudgeLooks(
        LookOutcome looks,
        LookPanel panel,
        List<CarryoverProposal> carryover,
        IReadOnlyList<Guid> notFound,
        IReadOnlyDictionary<Guid, HoldPositionNorm> warp,
        List<Guid>? unpairedNew)
    {
        if (presenceProbe is not { } probe || panel.OldPhoto is not { } oldPhoto
            || OverlapSeedLoader.RawSize(panel.NewPhoto) is not { } newSize || OverlapSeedLoader.RawSize(oldPhoto) is not { } oldSize)
        {
            return;
        }

        // A virtual hold has no pixels of its own: its look says nothing.
        var oldById = panel.OldHolds.Where(h => !h.IsVirtual).ToDictionary(h => h.Id);
        var stagedById = panel.Staged.ToDictionary(h => h.Id);
        var pairs = carryover.Where(p => oldById.ContainsKey(p.OldHoldId) && stagedById.ContainsKey(p.NewHoldId)).ToList();
        var missing = notFound.Where(id => oldById.ContainsKey(id) && warp.ContainsKey(id)).Distinct().ToList();
        var spots = pairs
            .Select(p => (New: Pixels(stagedById[p.NewHoldId].X, stagedById[p.NewHoldId].Y, newSize), Old: Pixels(oldById[p.OldHoldId].X, oldById[p.OldHoldId].Y, oldSize)))
            .ToList();
        var newToOld = spots.Select(s => new PointPair(s.New.X, s.New.Y, s.Old.X, s.Old.Y)).ToList();
        var queries = spots
            .Concat(missing.Select(id => (New: Pixels(warp[id].X, warp[id].Y, newSize), Old: Pixels(oldById[id].X, oldById[id].Y, oldSize))))
            .Select(s => HoldLookCheck.Query(newToOld, s.New, s.Old, newSize))
            .ToList();
        if (ScoreLooks(probe, oldPhoto, panel, queries) is not { } scores)
        {
            return;
        }

        var replaced = ApplyLooks(looks, panel.PanelId, pairs, scores, carryover, unpairedNew);
        for (var i = 0; i < missing.Count; i++)
        {
            if (scores[pairs.Count + i] is { } score)
            {
                looks.SpotScores[missing[i]] = score;
            }
        }

        logger.LogInformation(
            "Look check on panel {PanelId}: {Replaced} of {Pairs} pairs show another hold; {Probed} holds without a twin probed",
            panel.PanelId, replaced, pairs.Count, missing.Count);
    }

    /// <summary>Records the pairs that are not clearly the same and takes the replaced ones apart; returns how many were replaced.</summary>
    private int ApplyLooks(
        LookOutcome looks,
        Guid panelId,
        IReadOnlyList<CarryoverProposal> pairs,
        double?[] scores,
        List<CarryoverProposal> carryover,
        List<Guid>? unpairedNew)
    {
        var replaced = 0;
        for (var i = 0; i < pairs.Count; i++)
        {
            var look = HoldLookCheck.Judge(scores[i], moveOptions);
            if (look == HoldLook.Same)
            {
                continue;
            }

            looks.Findings.Add(new HoldLookFinding(pairs[i].OldHoldId, pairs[i].NewHoldId, panelId, look, scores[i]));
            if (look == HoldLook.Replaced)
            {
                carryover.Remove(pairs[i]);
                unpairedNew?.Add(pairs[i].NewHoldId);
                replaced++;
            }
        }

        return replaced;
    }

    /// <summary>
    /// One score per query (null where there was no query or the probe could not measure); null when the probe failed, in
    /// which case nothing on the panel is judged: a broken probe must neither take pairs apart nor mark boulders.
    /// </summary>
    private double?[]? ScoreLooks(IHoldPresenceProbe probe, byte[] oldPhoto, LookPanel panel, IReadOnlyList<PresenceQuery?> queries)
    {
        var asked = Enumerable.Range(0, queries.Count).Where(i => queries[i] is not null).ToList();
        var scores = new double?[queries.Count];
        if (asked.Count == 0)
        {
            return scores;
        }

        try
        {
            var measured = probe.Score(oldPhoto, panel.NewPhoto, asked.Select(i => queries[i]!.Value).ToList());
            for (var k = 0; k < asked.Count && k < measured.Count; k++)
            {
                scores[asked[k]] = measured[k];
            }

            return scores;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Look check failed on panel {PanelId}; its pairs stay as matched and no boulder is marked for it", panel.PanelId);
            return null;
        }
    }

    /// <summary>
    /// The old holds whose boulders are marked for review at the promote: replaced ones, and those without a twin whose
    /// predicted spot no longer shows them. A hold that is still there, or that could not be measured (outside the new
    /// photo, an unaligned panel), is left out. The replaced holds join <paramref name="removedCandidates"/> here, after
    /// the hand-placed merge, so that merge cannot pair them with the same detection again.
    /// </summary>
    private Dictionary<Guid, HoldMoveOutcome> CollectNotFoundAgain(LookOutcome looks, List<Guid> removedCandidates)
    {
        var result = new Dictionary<Guid, HoldMoveOutcome>();
        foreach (var id in removedCandidates)
        {
            if (looks.SpotScores.TryGetValue(id, out var score) && !HoldLookCheck.StillThere(score, moveOptions))
            {
                result[id] = HoldMoveOutcome.NotFound;
            }
        }

        foreach (var id in looks.Findings.Where(f => f.Look == HoldLook.Replaced).Select(f => f.OldHoldId).Distinct())
        {
            result[id] = HoldMoveOutcome.Replaced;
            removedCandidates.Add(id);
        }

        return result;
    }

    /// <summary>
    /// A detection freed from a replaced pair is a new hold. The triage would drop it for the very reason it was paired:
    /// the wall's 3D model knows a hold at that spot (the old one), or the surroundings look unchanged. Those two reasons
    /// are withdrawn for it; a marker, a spot the centre photo owns or a spot off the wall still drop it.
    /// </summary>
    private static void KeepReplacements(LookOutcome looks, Dictionary<Guid, NewHoldDiscardReason> discards)
    {
        foreach (var finding in looks.Findings.Where(f => f.Look == HoldLook.Replaced))
        {
            if (discards.TryGetValue(finding.NewHoldId, out var reason)
                && reason is NewHoldDiscardReason.KnownHoldIn3D or NewHoldDiscardReason.UnchangedSinceOldPhoto)
            {
                discards.Remove(finding.NewHoldId);
            }
        }
    }

    private static (double X, double Y) Pixels(double x, double y, (int Width, int Height) size) => (x * size.Width, y * size.Height);
}
