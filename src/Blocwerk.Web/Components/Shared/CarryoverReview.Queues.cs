// <copyright file="CarryoverReview.Queues.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The review QUEUES: which holds each focused stepper walks, in which order, and which of them the
/// user is spared. Two rules apply to every one of them — holds that a live boulder is built from come
/// FIRST (<see cref="HoldReviewOrdering"/>), and holds somebody has already reviewed are left out
/// until they are explicitly un-reviewed (<c>CarryoverReview.Reviewed.cs</c>). Split out of the main
/// partial so both rules live in one place and that file stays inside the project's size limit.
/// </summary>
public partial class CarryoverReview
{
    // ---- Review item lists (walked one at a time) ------------------------------
    // Every item carries the PERSISTED decision (Kind + NewHoldId) so the stepper can reflect prior
    // state on reopen instead of resetting to a blank "carried". _decisions is the source of truth.
    private CarryReviewItem ItemForOld(Guid oldId)
    {
        var d = _decisions.GetValueOrDefault(oldId);
        return new CarryReviewItem(oldId, d?.NewHoldId, d?.Kind ?? CarryKind.Carried);
    }

    // Every queue below is ordered boulder-holds-first (the consequential work) and stripped of the
    // holds somebody has already reviewed.
    private List<CarryReviewItem> CarriedItems =>
        HoldReviewOrdering.BoulderFirst(
                ExcludeReviewed(
                    DisplayedDecisions.Where(d => d.Kind is CarryKind.Carried or CarryKind.Changed),
                    d => d.OldHoldId),
                d => d.OldHoldId,
                _boulderHoldIds)
            .Select(d => new CarryReviewItem(d.OldHoldId, d.NewHoldId, d.Kind)).ToList();

    // The matcher reports removal candidates for EVERY re-photographed panel (its per-panel carryover
    // pass). Only the displayed panel's can be drawn over this photo, so only those are offered here.
    private List<Guid> DisplayedRemovedCandidates =>
        Session.RemovedCandidateHoldIds.Where(_displayedOldIds.Contains).ToList();

    private List<CarryReviewItem> RemovalItems =>
        HoldReviewOrdering.BoulderFirst(
                ExcludeReviewed(DisplayedRemovedCandidates, id => id),
                id => id,
                _boulderHoldIds)
            .Select(ItemForOld).ToList();

    // Staged NEW holds belong to no boulder yet, so the boulder-first pass is a no-op here today; it
    // stays so every queue is ordered by the same rule rather than by accident of the source list.
    private List<CarryReviewItem> NewItems =>
        HoldReviewOrdering.BoulderFirst(
                Session.NewCenterHoldIds.Where(id => _newHolds.Any(h => h.Id == id)),
                id => id,
                _boulderHoldIds)
            .Select(id => new CarryReviewItem(null, id)).ToList();

    // The stepper walks a FROZEN membership (see OpenReview) so that confirming an item — which drops
    // it out of the live lists — cannot shift the index under the user mid-walk. The items themselves
    // are re-derived from _decisions on every render, so each one still shows its current verdict.
    private List<CarryReviewItem> ReviewItems => _reviewMode switch
    {
        CarryReviewMode.New => (_newWalkOrder ?? []).Select(id => new CarryReviewItem(null, id)).ToList(),
        not null => (_walkOrder ?? []).Select(ItemForOld).ToList(),
        _ => [],
    };

    // The old-hold ids the open stepper is walking, captured when it opened.
    private List<Guid>? _walkOrder;

    // The same freeze for the new-hold walk. It used to read NewItems live, which was harmless while
    // that list could only shrink from outside the stepper — but the stepper now carries the bin tool,
    // and deleting a staged hold mid-walk would drop its entry and shift every later item one place
    // under the index. The frozen list KEEPS the entry instead (the stepper disables keep/discard on
    // it, see CurrentNewHoldGone). The overview's own count still reads NewItems live, exactly like
    // CarriedToReview does, so the counts track the edits while the walk stays put.
    private List<Guid>? _newWalkOrder;

    // The decision handlers and the as-you-go saves live in CarryoverReview.Persistence.cs.
    private void OpenReview(CarryReviewMode mode)
    {
        _reviewMode = mode;
        _walkOrder = mode switch
        {
            CarryReviewMode.Uncertain => [.. _attentionQueue],
            CarryReviewMode.Carried => CarriedItems.Where(i => i.OldHoldId is not null).Select(i => i.OldHoldId!.Value).ToList(),
            CarryReviewMode.Removal => RemovalItems.Where(i => i.OldHoldId is not null).Select(i => i.OldHoldId!.Value).ToList(),
            _ => null,
        };
        _newWalkOrder = mode == CarryReviewMode.New
            ? NewItems.Where(i => i.NewHoldId is not null).Select(i => i.NewHoldId!.Value).ToList()
            : null;
    }

    private void CloseReview()
    {
        _reviewMode = null;
        _walkOrder = null;
        _newWalkOrder = null;
    }
}
