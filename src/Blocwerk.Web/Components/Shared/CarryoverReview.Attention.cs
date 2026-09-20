// <copyright file="CarryoverReview.Attention.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The attention queue: derives the FEW old holds that actually need the user's eyes so the confirm
/// UI never asks them to sign off hundreds of correctly auto-carried holds. Also holds the cross-gen
/// linking seam (the in-memory old→new mapping over <c>_decisions</c>) and the phase switching for the
/// side-by-side <see cref="CrossGenLinkTool"/> and the residual-ranked <see cref="CarryoverStepper"/>.
/// </summary>
public partial class CarryoverReview
{
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

    // Residual-ranked (descending) old holds the matcher was not confident about. Static per session
    // (matcher output does not change), so it is computed once after seeding and reused for the
    // headline count, the side-by-side focus, and the Uncertain stepper queue.
    private List<Guid> _attentionQueue = [];

    private bool _crossGenOpen;

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

        return scored.OrderByDescending(s => s.Residual).Select(s => s.Id).ToList();
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
    private int NeedsReviewCount => _attentionQueue.Count;
    private int TotalOldCount => _oldHolds.Count;
    private int AutoCarriedCount => Math.Max(0, TotalOldCount - NeedsReviewCount);

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
            var released = d with { NewHoldId = null };
            _decisions[oldId] = released;
            changed.Add(released);
        }

        var cur = _decisions.GetValueOrDefault(e.OldHoldId);
        // Linking a hold asserts it is present, so a stray "removed" reverts to carried.
        var kind = cur is null || cur.Kind == CarryKind.Removed ? CarryKind.Carried : cur.Kind;
        var linked = new CarryoverDecision(e.OldHoldId, kind, e.NewHoldId);
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
            var unlinked = d with { NewHoldId = null };
            _decisions[oldId] = unlinked;
            await SaveCarryAsync(unlinked);
        }
    }

    // ---- Phase switching -----------------------------------------------------------
    private void OpenCrossGen() => _crossGenOpen = true;

    private void CloseCrossGen() => _crossGenOpen = false;

    // Hand off from the side-by-side confirm (Step 2) to the changed/moved review (Step 3), walking
    // the same residual-ranked attention queue.
    private void AdvanceToChangedReview()
    {
        _crossGenOpen = false;
        OpenReview(CarryReviewMode.Uncertain);
    }

    // The residual-ranked attention queue as stepper items, each carrying its persisted decision.
    private List<CarryReviewItem> UncertainItems => _attentionQueue.Select(ItemForOld).ToList();
}
