// <copyright file="CarryoverReview.Attention.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The attention queue: derives the FEW old holds that actually need the user's eyes so the confirm
/// UI never asks them to sign off hundreds of correctly auto-carried holds. Also holds the cross-gen
/// linking seam (the in-memory old→new mapping over <c>_decisions</c>) and the phase switching for the
/// side-by-side <see cref="CrossGenLinkTool"/> and the residual-ranked <see cref="CarryoverStepper"/>.
/// </summary>
public partial class CarryoverReview
{
    [Inject]
    private IBoulderService BoulderService { get; set; } = default!;

    // ---- Attention-queue thresholds (named + easy to tune) --------------------------
    // Mirrors the matcher's per-hold carry gate; a residual past HighResidualFactor× this gate reads
    // as the matcher's implicit "this hold moved" and is surfaced for confirmation.
    private const double MatchGatePx = 85.0;
    private const double HighResidualFactor = 0.6;
    private const double HighResidualPx = MatchGatePx * HighResidualFactor;

    // A proposal below this confidence is treated as uncertain (the matcher's auto-accept cutoff).
    private const double LowConfidenceThreshold = 0.45;

    // An old hold with NO proposal at all is carried blind (warp-predicted) — the top of the queue.
    private const double NoProposalRank = double.PositiveInfinity;

    // The old holds still needing eyes: the matcher was unsure about them AND nobody has reviewed them
    // yet. Boulder holds first, then residual (descending). The matcher half is static per session, the
    // reviewed half is not — so this is REBUILT after every decision that can confirm one (see
    // SaveCarryAsync). The show-reviewed toggle does NOT rebuild it: that toggle lists reviewed holds,
    // it never returns one to a queue or a count. It drives the headline count, the
    // side-by-side focus and the Uncertain stepper queue, which is exactly why it has to move as the
    // user works: a count that never shrinks tells them nothing about what is left.
    private List<Guid> _attentionQueue = [];

    // Old holds that at least one LIVE boulder is built from. The reason to review a hold at all: a
    // mis-carried hold under a boulder freezes it, a mis-carried unused hold costs nothing. Loaded ONCE
    // per component from IBoulderService.GetHoldUsageAsync (one query, already twin-expanded) and then
    // only read — see LoadBoulderHoldsAsync.
    private HashSet<Guid> _boulderHoldIds = [];

    private bool _crossGenOpen;

    /// <summary>
    /// Loads the wall's hold→boulder usage once, reduced to the set of holds carrying a live boulder.
    /// A failure leaves the set empty, which only costs the boulder-first ORDER — never an item.
    /// </summary>
    private async Task LoadBoulderHoldsAsync()
    {
        try
        {
            var usage = await BoulderService.GetHoldUsageAsync(WallId);
            _boulderHoldIds = HoldReviewOrdering.LiveBoulderHoldIds(usage);
        }
        catch (Exception)
        {
            _boulderHoldIds = [];
        }
    }

    private List<Guid> BuildAttentionQueue()
    {
        var byOld = ProposalsByOld();
        var scored = new List<(Guid Id, double Residual)>();
        foreach (var h in _oldHolds)
        {
            if (byOld.TryGetValue(h.Id, out var p))
            {
                if (p.Confidence < LowConfidenceThreshold || p.ResidualPx > HighResidualPx)
                {
                    scored.Add((h.Id, p.ResidualPx));
                }
            }
            else
            {
                scored.Add((h.Id, NoProposalRank));
            }
        }

        // Reviewed holds leave the queue (un-reviewing is the only way back in), and holds that carry a
        // boulder outrank everything else regardless of residual.
        return HoldReviewOrdering.BoulderFirstThenByDescending(
                ExcludeReviewed(scored, s => s.Id),
                s => s.Id,
                s => s.Residual,
                _boulderHoldIds)
            .Select(s => s.Id)
            .ToList();
    }

    private Dictionary<Guid, CarryoverProposal> ProposalsByOld()
    {
        var map = new Dictionary<Guid, CarryoverProposal>();
        foreach (var p in Session.Carryover)
        {
            map[p.OldHoldId] = p;
        }

        return map;
    }

    // ---- Headline framing ----------------------------------------------------------
    // All three move as the user works: the queue is rebuilt after every decision, so a hold that has
    // just been reviewed leaves NeedsReviewCount and joins ReviewedCount.
    private int NeedsReviewCount => _attentionQueue.Count;
    private int TotalOldCount => _oldHolds.Count;
    private int AutoCarriedCount => Math.Max(0, TotalOldCount - NeedsReviewCount - ReviewedCount);

    // ---- Cross-gen linking seam (in-memory over _decisions) ------------------------
    // The current old→new mapping the side-by-side renders as pre-linked (green). Only carried holds
    // with a twin appear; a removed hold has no cross-gen link.
    // Scoped to the DISPLAYED panel, like every other derivation off _decisions: CrossGenLinkTool's
    // OldHolds/NewHolds are the centre sets, so a co-updated neighbour's link could only ever be a
    // lookup miss there. Reading DisplayedDecisions keeps the two scopes from meeting at all rather
    // than relying on that miss staying harmless.
    private IReadOnlyDictionary<Guid, Guid> CrossGenLinks()
    {
        var map = new Dictionary<Guid, Guid>();
        foreach (var d in DisplayedDecisions)
        {
            if (d.Kind != CarryKind.Removed && d.NewHoldId is { } nid)
            {
                map[d.OldHoldId] = nid;
            }
        }

        return map;
    }

    // A link (and the claims it releases) is a real decision, so it is written through as it is made
    // — the side-by-side tool is where most of the manual mapping happens, and losing it to a closed
    // browser is exactly what the session exists to prevent.
    private async Task LinkCrossGen((Guid OldHoldId, Guid NewHoldId) e)
    {
        // A staged new hold is the twin of at most ONE old hold — release any prior claim on it so we
        // never carry two old holds onto the same new position.
        var stealers = _decisions
            .Where(kv => kv.Value.NewHoldId == e.NewHoldId && kv.Key != e.OldHoldId)
            .ToList();
        var changed = new List<CarryoverDecision>();
        foreach (var (oldId, d) in stealers)
        {
            // The release is a side effect of somebody else's link, never a review of THIS hold: write
            // it unconfirmed so the verdict change drops any earlier sign-off and the hold comes back
            // into the queue carrying blind.
            var released = d with { NewHoldId = null, Confirmed = false };
            _decisions[oldId] = released;
            changed.Add(released);
        }

        var cur = _decisions.GetValueOrDefault(e.OldHoldId);
        // Linking a hold asserts it is present, so a stray "removed" reverts to carried.
        var kind = cur is null || cur.Kind == CarryKind.Removed ? CarryKind.Carried : cur.Kind;
        // Pointing at the hold's real counterpart is as deliberate as a decision gets, so the link
        // confirms the verdict and the hold leaves the review queues.
        var linked = new CarryoverDecision(e.OldHoldId, kind, e.NewHoldId, Confirmed: true);
        _decisions[e.OldHoldId] = linked;
        changed.Add(linked);

        await SaveCarryAllAsync(changed);
    }

    private async Task UnlinkCrossGen(Guid oldId)
    {
        // Breaking a mapping is always safe: the hold falls back to blind warp-carry (still carried,
        // never lost) — the "never lose a hold" invariant is preserved.
        if (_decisions.TryGetValue(oldId, out var d))
        {
            // Unconfirmed, exactly like the stealer release above and for the same reason: breaking the
            // match says only "that twin is wrong", never where the hold actually is, and it leaves the
            // hold carried BLIND. Riding the old sign-off along would let a hold that just entered the
            // riskiest state read as reviewed and leave the queues — and re-stamp the breaker as its
            // confirmer. False lets the policy's verdict-changed rule drop the sign-off the vanished
            // match was about; a hold that had no twin to begin with keeps whatever it had.
            var unlinked = d with { NewHoldId = null, Confirmed = false };
            _decisions[oldId] = unlinked;
            await SaveCarryAsync(unlinked);
        }
    }

    // ---- Phase switching -----------------------------------------------------------
    // The side-by-side walks a FROZEN copy of the queue, for the same reason the stepper does: a link
    // confirms its hold, which drops it out of the live queue, and a list shrinking under the focus
    // index would silently skip the next hold.
    private List<Guid> _crossGenQueue = [];

    private void OpenCrossGen()
    {
        _crossGenQueue = [.. _attentionQueue];
        _crossGenOpen = true;
    }

    private void CloseCrossGen()
    {
        _crossGenOpen = false;
        _crossGenQueue = [];
    }

    // Hand off from the side-by-side confirm (Step 2) to the changed/moved review (Step 3), walking
    // the same residual-ranked attention queue.
    private void AdvanceToChangedReview()
    {
        _crossGenOpen = false;
        OpenReview(CarryReviewMode.Uncertain);
    }
}
