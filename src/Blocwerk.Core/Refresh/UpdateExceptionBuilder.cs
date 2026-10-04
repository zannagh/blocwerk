// <copyright file="UpdateExceptionBuilder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// The confirm screen's cards for the quick review's decisions: what needs a person's look, everything else stays in the
/// summary. Only holds whose default decision the evidence is about become cards: an old hold possibly removed that is
/// carried in place, a match carried onto an unsure twin, a detection the photo check leaves out while the 3D model sees
/// a hold there. Pure.
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

        foreach (var p in session.Carryover.Where(p => p.Confidence < LowConfidence).OrderBy(p => p.Confidence))
        {
            if (carry.GetValueOrDefault(p.OldHoldId) is { Kind: CarryKind.Carried } d && d.NewHoldId == p.NewHoldId)
            {
                drafts.Add(new UpdateExceptionDraft(UpdateExceptionKind.LowConfidenceMatch, p.OldHoldId, p.NewHoldId, Confidence: p.Confidence));
            }
        }

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
}
