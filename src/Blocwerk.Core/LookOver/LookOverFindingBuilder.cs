// <copyright file="LookOverFindingBuilder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.LookOver;

/// <summary>What a promote knows when it is about to commit, as far as the look-over needs it.</summary>
/// <param name="Cards">The confirm screen's cards of the promoted session.</param>
/// <param name="Carry">The promoted carry decision per old hold.</param>
/// <param name="Successors">Old hold to the live hold it became.</param>
/// <param name="OldHolds">The old holds of the re-photographed panels.</param>
/// <param name="LiveHolds">Every hold of the new generation, after the promote.</param>
/// <param name="StagedHolds">Every staged hold of the re-photographed panels, whether it went live or was left out.</param>
/// <param name="NotFound">Old holds kept in place whose boulders the promote marked (not found again, or replaced).</param>
/// <param name="Boulders">Live hold to the number of current boulders the promote moved onto it.</param>
/// <param name="PhotoSizes">The new photos' pixel sizes per panel.</param>
internal sealed record LookOverPromoteFacts(
    IReadOnlyList<WallUpdateException> Cards,
    IReadOnlyDictionary<Guid, CarryoverDecision> Carry,
    IReadOnlyDictionary<Guid, Guid> Successors,
    IReadOnlyDictionary<Guid, Hold> OldHolds,
    IReadOnlyDictionary<Guid, Hold> LiveHolds,
    IReadOnlyDictionary<Guid, Hold> StagedHolds,
    IReadOnlyDictionary<Guid, HoldMoveOutcome> NotFound,
    IReadOnlyDictionary<Guid, int> Boulders,
    IReadOnlyDictionary<Guid, (int Width, int Height)> PhotoSizes);

/// <summary>
/// The findings an applied update leaves for the look-over: every card nobody answered whose default is what went live,
/// the holds on boulders that were not found again, and old holds kept in place under a new hold. One finding per old
/// hold; a hold a person decided (an answered card, a confirmed verdict in the full review) is not a finding. Pure.
/// </summary>
internal static class LookOverFindingBuilder
{
    /// <summary>The findings of one promote (wall, session and generation are the caller's to set).</summary>
    /// <param name="facts">What the promote did.</param>
    /// <returns>The findings.</returns>
    public static List<WallLookOverFinding> Build(LookOverPromoteFacts facts)
    {
        var findings = new List<WallLookOverFinding>();
        foreach (var card in facts.Cards.Where(c => c.Status == UpdateExceptionStatus.Pending))
        {
            if (FromCard(facts, card) is { } finding && !findings.Any(f => f.OldHoldId is { } o && o == finding.OldHoldId))
            {
                findings.Add(finding);
            }
        }

        AddNotFound(facts, findings);
        AddSameSpots(facts, findings);
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

        return About(facts, finding, oldId, live);
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
            if (known is null ? Confirmed(facts, oldId) : known.OtherHoldId is not null || !LookOverRules.CanHaveOther(known.Kind))
            {
                continue;
            }

            var size = facts.PhotoSizes.TryGetValue(panel, out var s) ? s : ((int Width, int Height)?)null;
            if (SameSpotFinder.Nearest(kept, fresh[panel], size) is not { } other)
            {
                continue;
            }

            if (known is not null)
            {
                known.OtherHoldId = other.Id;
                continue;
            }

            var finding = About(facts, At(LookOverFindingKind.SameSpot, other), oldId, kept);
            finding.OtherHoldId = other.Id;
            findings.Add(finding);
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

    private static WallLookOverFinding About(LookOverPromoteFacts facts, WallLookOverFinding finding, Guid oldId, Hold live)
    {
        var decision = facts.Carry.GetValueOrDefault(oldId);
        var wasFlagged = facts.OldHolds.GetValueOrDefault(oldId)?.NeedsReview ?? true;
        finding.OldHoldId = oldId;
        finding.HoldId = live.Id;
        finding.PanelId ??= live.WallPanelId;
        finding.BouldersAtApply = facts.Boulders.GetValueOrDefault(live.Id);
        finding.FlaggedHold = live.NeedsReview && !wasFlagged && decision?.Kind != CarryKind.Changed;
        return finding;
    }
}
