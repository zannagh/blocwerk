// <copyright file="UpdateExceptionBuilder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// The confirm screen's cards for the quick review's decisions: what needs a person's look, everything else stays in the
/// summary. Only holds whose default decision the evidence is about become cards: an old hold possibly removed that is
/// carried in place, an old hold whose spot shows another hold now, a match carried onto an unsure twin, a detection
/// the photo check leaves out while the 3D model sees a hold there. Pure.
/// </summary>
public static class UpdateExceptionBuilder
{
    /// <summary>A carry match below this confidence is shown as a card (the full review's own "uncertain" bar).</summary>
    public const double LowConfidence = 0.45;

    /// <summary>The cards for <paramref name="quick"/>, made from <paramref name="session"/>.</summary>
    /// <param name="session">The matched session (with its 3D evidence, when it had any).</param>
    /// <param name="quick">The quick review's decisions.</param>
    /// <returns>The cards.</returns>
    public static IReadOnlyList<UpdateExceptionDraft> Build(BigUpdateSession session, QuickDecisions quick)
    {
        var carry = quick.Carryover.GroupBy(d => d.OldHoldId).ToDictionary(g => g.Key, g => g.First());
        var drafts = new List<UpdateExceptionDraft>();
        foreach (var r in session.PossiblyRemoved ?? [])
        {
            if (carry.GetValueOrDefault(r.OldHoldId) is { Kind: CarryKind.Carried, NewHoldId: null })
            {
                drafts.Add(new UpdateExceptionDraft(
                    UpdateExceptionKind.PossiblyRemoved, r.OldHoldId, null, r.PanelId, r.X, r.Y, r.ModelId, r.FacetId, r.A, r.B,
                    r.PhotoScore, r.TextureScore));
            }
        }

        AddMatchCards(session, carry, drafts);

        foreach (var m in session.HandPlacedMerges ?? [])
        {
            if (carry.GetValueOrDefault(m.OldHoldId) is { Kind: CarryKind.Carried } d && d.NewHoldId == m.NewHoldId)
            {
                drafts.Add(new UpdateExceptionDraft(UpdateExceptionKind.MatchedToHandPlaced, m.OldHoldId, m.NewHoldId));
            }
        }

        foreach (var a in session.HandPlacedAmbiguous ?? [])
        {
            drafts.Add(new UpdateExceptionDraft(UpdateExceptionKind.HandPlacedAmbiguous, a.OldHoldId, a.NewHoldIds[0]));
        }

        var leftOut = quick.RemovedNewCentreHoldIds.Concat(quick.Neighbours.SelectMany(n => n.RemovedNeighbourHoldIds)).ToHashSet();
        foreach (var c in (session.ConflictingNew ?? []).Where(c => leftOut.Contains(c.StagedHoldId)))
        {
            drafts.Add(new UpdateExceptionDraft(
                UpdateExceptionKind.ConflictingNew, null, c.StagedHoldId, c.PanelId, ModelId: c.ModelId, FacetId: c.FacetId, A: c.A, B: c.B));
        }

        return drafts;
    }

    /// <summary>
    /// The cards about what an old hold was paired with: another hold sits at its spot now (not carried onto it, kept in
    /// place), or the pair is carried although the matcher or the look check (<see cref="HoldLookCheck"/>) is unsure.
    /// </summary>
    private static void AddMatchCards(BigUpdateSession session, Dictionary<Guid, CarryoverDecision> carry, List<UpdateExceptionDraft> drafts)
    {
        var looks = session.Looks ?? [];
        var keptInPlace = looks.Where(f => f.Look == HoldLook.Replaced
            && carry.GetValueOrDefault(f.OldHoldId) is { Kind: CarryKind.Carried, NewHoldId: null });
        foreach (var f in keptInPlace)
        {
            drafts.Add(new UpdateExceptionDraft(UpdateExceptionKind.ReplacedByOther, f.OldHoldId, f.NewHoldId, f.PanelId, PhotoScore: f.Score));
        }

        var unsure = looks.Where(f => f.Look == HoldLook.Unsure).ToLookup(f => (f.OldHoldId, f.NewHoldId));
        var doubtful = session.Carryover
            .Where(p => p.Confidence < LowConfidence || unsure.Contains((p.OldHoldId, p.NewHoldId)))
            .Where(p => carry.GetValueOrDefault(p.OldHoldId) is { Kind: CarryKind.Carried } d && d.NewHoldId == p.NewHoldId)
            .OrderBy(p => p.Confidence);
        foreach (var p in doubtful)
        {
            var score = unsure[(p.OldHoldId, p.NewHoldId)].FirstOrDefault()?.Score;
            drafts.Add(new UpdateExceptionDraft(UpdateExceptionKind.LowConfidenceMatch, p.OldHoldId, p.NewHoldId, PhotoScore: score, Confidence: p.Confidence));
        }
    }
}
