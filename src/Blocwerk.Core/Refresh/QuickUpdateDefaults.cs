// <copyright file="QuickUpdateDefaults.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// The quick review's decisions: exactly what the step-by-step review starts from when nobody changes anything
/// (every old hold carried, onto the matcher's twin where there is one; new detections kept unless the service
/// suggested dropping them), plus the overlap links the matcher is at least <see cref="OverlapThreshold"/> sure of.
/// Nothing is ever removed or marked as moved here: that stays a person's decision in the full review.
/// </summary>
public static class QuickUpdateDefaults
{
    /// <summary>Overlap suggestions at or above this confidence are linked without asking.</summary>
    public const double OverlapThreshold = 0.9;

    /// <summary>Builds the decisions from the matched session and the staged hold ids per staged panel.</summary>
    public static QuickDecisions Build(BigUpdateSession session, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> stagedHoldsByPanel)
    {
        var suggested = session.SuggestedNewDiscards ?? new Dictionary<Guid, NewHoldDiscardReason>();
        var twins = CarryoverScope.DefaultTwins(session);
        var carry = (session.CarriedOldHoldIds ?? [])
            .Select(id => new CarryoverDecision(id, CarryKind.Carried, twins.TryGetValue(id, out var twin) ? twin : null))
            .ToList();

        var consumed = carry.Where(d => d.NewHoldId is not null).Select(d => d.NewHoldId!.Value).ToHashSet();
        var centre = stagedHoldsByPanel.GetValueOrDefault(session.CenterPanelId) ?? [];
        var free = centre.Where(id => !consumed.Contains(id)).ToList();
        var accepted = free.Where(id => !suggested.ContainsKey(id)).ToList();
        var removed = free.Where(suggested.ContainsKey).ToList();

        var neighbours = session.Neighbours
            .Select(n => NeighbourDefaults(n, stagedHoldsByPanel.GetValueOrDefault(n.PanelId) ?? [], suggested))
            .ToList();
        var leftOut = session.Neighbours.Sum(n => n.Proposals.Count) - neighbours.Sum(n => n.Links.Count);
        return new QuickDecisions(carry, accepted, removed, neighbours, Math.Max(0, leftOut));
    }

    private static NeighbourLinkSet NeighbourDefaults(
        NeighbourOverlap overlap, IReadOnlyList<Guid> stagedHolds, IReadOnlyDictionary<Guid, NewHoldDiscardReason> suggested)
    {
        var removed = stagedHolds.Where(suggested.ContainsKey).ToList();
        var removedSet = removed.ToHashSet();
        var usedA = new HashSet<Guid>();
        var usedB = new HashSet<Guid>();
        var links = new List<ConfirmedLink>();
        foreach (var p in overlap.Proposals.Where(p => p.Confidence >= OverlapThreshold && !p.Moved).OrderByDescending(p => p.Confidence))
        {
            if (suggested.ContainsKey(p.HoldAId) || removedSet.Contains(p.HoldBId) || !usedA.Add(p.HoldAId))
            {
                continue;
            }

            if (!usedB.Add(p.HoldBId))
            {
                usedA.Remove(p.HoldAId);
                continue;
            }

            links.Add(new ConfirmedLink(p.HoldAId, p.HoldBId, Moved: false));
        }

        return new NeighbourLinkSet(overlap.PanelId, links, removed);
    }
}

/// <summary>The quick review's decisions, in the shapes the update session stores.</summary>
/// <param name="Carryover">One verdict per old hold (all carried).</param>
/// <param name="AcceptedNewCentreHoldIds">New centre holds kept.</param>
/// <param name="RemovedNewCentreHoldIds">Centre detections dropped (the service's suggestions).</param>
/// <param name="Neighbours">Per neighbour panel: the sure links and the dropped detections.</param>
/// <param name="OverlapsLeftOut">Overlap suggestions not linked (below the threshold or marked as moved).</param>
public sealed record QuickDecisions(
    IReadOnlyList<CarryoverDecision> Carryover,
    IReadOnlyList<Guid> AcceptedNewCentreHoldIds,
    IReadOnlyList<Guid> RemovedNewCentreHoldIds,
    IReadOnlyList<NeighbourLinkSet> Neighbours,
    int OverlapsLeftOut);
