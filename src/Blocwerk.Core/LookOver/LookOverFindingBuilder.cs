// <copyright file="LookOverFindingBuilder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// The findings an applied update leaves for the look-over: every card nobody answered whose default is what went live,
/// and every old hold that was kept in place without being found again, whether a boulder uses it, a new hold sits
/// on its spot, or neither. One finding per old hold; a hold a person decided (an answered card, a confirmed verdict
/// in the full review) is not a finding. Pure.
/// </summary>
internal static class LookOverFindingBuilder
{
    /// <summary>The findings of one promote (wall, session and generation are the caller's to set).</summary>
    /// <param name="facts">What the promote did.</param>
    /// <returns>The findings.</returns>
    public static List<WallLookOverFinding> Build(LookOverPromoteFacts facts)
    {
        // Two cards about one old hold: the more consequential kind wins, whatever order the rows come in.
        var cards = facts.Cards
            .Where(c => c.Status == UpdateExceptionStatus.Pending)
            .OrderBy(c => LookOverRules.Rank((LookOverFindingKind)(int)c.Kind))
            .ThenBy(c => c.Id);
        var seenOldHolds = new HashSet<Guid>();
        var findings = cards
            .Select(card => FromCard(facts, card))
            .OfType<WallLookOverFinding>()
            .Where(f => f.OldHoldId is not { } old || seenOldHolds.Add(old))
            .ToList();

        AddNotFound(facts, findings);
        AddSameSpots(facts, findings);
        AddKept(facts, findings);
        return findings;
    }

    private static WallLookOverFinding? FromCard(LookOverPromoteFacts facts, WallUpdateException card)
    {
        if (card.Kind == UpdateExceptionKind.ConflictingNew)
        {
            // Only while it is still left out: a detection somebody kept is a normal new hold.
            return card.StagedHoldId is { } id && !facts.LiveHolds.ContainsKey(id) && facts.StagedHolds.TryGetValue(id, out var leftOut)
                ? WithEvidence(At(LookOverFindingKind.ConflictingNew, leftOut), card)
                : null;
        }

        if (card.OldHoldId is not { } oldId || !StillTheDefault(facts, card, oldId) || Live(facts, oldId) is not { } live)
        {
            return null;
        }

        var staged = card.StagedHoldId is { } stagedId ? facts.StagedHolds.GetValueOrDefault(stagedId) : null;
        var finding = WithEvidence(At((LookOverFindingKind)(int)card.Kind, staged ?? live), card);
        if (card is { Kind: UpdateExceptionKind.PossiblyRemoved, PanelId: { } panelId, X: { } x, Y: { } y })
        {
            (finding.PanelId, finding.X, finding.Y) = (panelId, x, y);
        }

        if (card.Kind is UpdateExceptionKind.ReplacedByOther or UpdateExceptionKind.HandPlacedAmbiguous
            && staged is not null && staged.Id != live.Id && facts.LiveHolds.ContainsKey(staged.Id))
        {
            finding.OtherHoldId = staged.Id;
        }

        About(facts, finding, oldId, live);
        finding.FlaggedHold = OwnsTheMark(facts, oldId, live);
        return finding;
    }

    /// <summary>The card still describes what was promoted: nobody confirmed, removed or re-paired the hold since.</summary>
    private static bool StillTheDefault(LookOverPromoteFacts facts, WallUpdateException card, Guid oldId)
    {
        var decision = facts.Carry.GetValueOrDefault(oldId) ?? new CarryoverDecision(oldId, CarryKind.Carried, null);
        if (decision.Kind == CarryKind.Removed || decision.Confirmed)
        {
            return false;
        }

        return card.Kind is UpdateExceptionKind.LowConfidenceMatch or UpdateExceptionKind.MatchedToHandPlaced
            ? decision.NewHoldId == card.StagedHoldId
            : decision.NewHoldId is null;
    }

    /// <summary>
    /// The hold's review mark is this finding's alone, so settling it may take the mark off: the carry set it for the
    /// unanswered card on a hold nothing had marked, and nothing marks it afterwards (a second old hold merged onto
    /// the same detection, a panel whose photo could not be lined up).
    /// </summary>
    private static bool OwnsTheMark(LookOverPromoteFacts facts, Guid oldId, Hold live) =>
        facts.CardMarked.Contains(oldId)
        && facts.Successors.Count(s => s.Value == live.Id) == 1
        && (live.WallPanelId is not { } panel || !facts.UnalignedPanelIds.Contains(panel));

    private static void AddNotFound(LookOverPromoteFacts facts, List<WallLookOverFinding> findings)
    {
        foreach (var (oldId, outcome) in facts.NotFound.OrderBy(n => n.Key))
        {
            if (findings.Any(f => f.OldHoldId == oldId) || Confirmed(facts, oldId) || Live(facts, oldId) is not { } live)
            {
                continue;
            }

            // Without a boulder nothing was marked: the hold is kept silently, as the confirm screen said.
            if (facts.Boulders.GetValueOrDefault(live.Id) == 0)
            {
                continue;
            }

            var kind = outcome == HoldMoveOutcome.Replaced ? LookOverFindingKind.ReplacedByOther : LookOverFindingKind.NotFound;
            findings.Add(About(facts, At(kind, live), oldId, live));
        }
    }

    /// <summary>
    /// An old hold that was kept in place as a copy, with a new hold on its spot: the finding about the old hold learns
    /// which new hold that is, and a copy no finding is about gets one of its own.
    /// </summary>
    private static void AddSameSpots(LookOverPromoteFacts facts, List<WallLookOverFinding> findings)
    {
        var successors = facts.Successors.Values.ToHashSet();
        var fresh = facts.LiveHolds.Values
            .Where(h => !successors.Contains(h.Id) && h is { WallPanelId: not null, IsVirtual: false })
            .ToLookup(h => h.WallPanelId!.Value);
        foreach (var (oldId, liveId) in facts.Successors.OrderBy(s => s.Key))
        {
            // Only a copy: a hold carried onto a detection IS that detection.
            if (facts.StagedHolds.ContainsKey(liveId) || !facts.LiveHolds.TryGetValue(liveId, out var kept) || kept is not { WallPanelId: { } panel, IsVirtual: false })
            {
                continue;
            }

            var known = findings.FirstOrDefault(f => f.OldHoldId == oldId);
            var size = facts.PhotoSizes.TryGetValue(panel, out var s) ? s : ((int Width, int Height)?)null;
            if (known is { OtherHoldId: { } named })
            {
                // The card already says which new hold it is: only whether the old one lies under it is still to tell.
                known.Covered = facts.LiveHolds.TryGetValue(named, out var given) && SameSpotFinder.Covers(given, kept, size);
                continue;
            }

            if (known is null ? Confirmed(facts, oldId) : !LookOverRules.CanHaveOther(known.Kind))
            {
                continue;
            }

            if (SameSpotFinder.Nearest(kept, fresh[panel].OrderBy(h => h.Id), size) is not { } other)
            {
                continue;
            }

            // Both pictures at the old hold's spot and at one scale (the larger hold's), so they can be compared.
            var finding = known ?? About(facts, At(LookOverFindingKind.SameSpot, kept), oldId, kept);
            finding.OtherHoldId = other.Id;
            finding.Covered = SameSpotFinder.Covers(other, kept, size);
            if (known is null)
            {
                finding.Radius = Math.Max(kept.Radius, other.Radius);
                findings.Add(finding);
            }
        }
    }

    /// <summary>
    /// Every hold the update kept without finding it again is a finding, also when no boulder uses it and nothing sits
    /// on its spot: it is on the wall as a copy nobody has confirmed (<see cref="Hold.KeptUnconfirmedGeneration"/>).
    /// </summary>
    private static void AddKept(LookOverPromoteFacts facts, List<WallLookOverFinding> findings)
    {
        var known = findings.Select(f => f.OldHoldId).OfType<Guid>().ToHashSet();
        foreach (var (oldId, liveId) in facts.Successors.OrderBy(s => s.Key))
        {
            if (!known.Contains(oldId) && facts.LiveHolds.TryGetValue(liveId, out var kept) && kept.KeptUnconfirmedGeneration is not null)
            {
                findings.Add(About(facts, At(LookOverFindingKind.NotFound, kept), oldId, kept));
            }
        }
    }

    private static bool Confirmed(LookOverPromoteFacts facts, Guid oldId) => facts.Carry.GetValueOrDefault(oldId) is { Confirmed: true };

    private static Hold? Live(LookOverPromoteFacts facts, Guid oldId) =>
        facts.Successors.TryGetValue(oldId, out var id) ? facts.LiveHolds.GetValueOrDefault(id) : null;

    private static WallLookOverFinding At(LookOverFindingKind kind, Hold spot) =>
        new() { Kind = kind, PanelId = spot.WallPanelId, X = spot.X, Y = spot.Y, Radius = spot.Radius };

    private static WallLookOverFinding WithEvidence(WallLookOverFinding finding, WallUpdateException card)
    {
        (finding.GeometryModelId, finding.FacetId, finding.A, finding.B) = (card.GeometryModelId, card.FacetId, card.A, card.B);
        (finding.PhotoScore, finding.TextureScore, finding.Confidence) = (card.PhotoScore, card.TextureScore, card.Confidence);
        return finding;
    }

    /// <summary>Ties a finding to the old hold and what it became. The review mark stays the hold's own unless a card says otherwise.</summary>
    private static WallLookOverFinding About(LookOverPromoteFacts facts, WallLookOverFinding finding, Guid oldId, Hold live)
    {
        finding.OldHoldId = oldId;
        finding.HoldId = live.Id;
        finding.PanelId ??= live.WallPanelId;
        finding.BouldersAtApply = facts.Boulders.GetValueOrDefault(live.Id);
        finding.HoldKept = live.KeptUnconfirmedGeneration is not null;

        // A hold not found again, or replaced by another, marks boulders by itself; every other kind explains no boulder's mark.
        finding.MarkedBoulderIds = finding.Kind switch
        {
            LookOverFindingKind.NotFound => facts.NewlyMarked.GetValueOrDefault(live.Id) ?? [],
            LookOverFindingKind.ReplacedByOther => facts.NewlyReplaced?.GetValueOrDefault(live.Id) ?? [],
            _ => [],
        };
        return finding;
    }
}
