// <copyright file="CarryoverReview.Persistence.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The durability seam of the carryover review: seeding the in-memory decisions from what the session
/// already recorded, and writing each verdict through the moment the user makes it. Saves happen on
/// meaningful actions only (a decision, a link, a discard) — never per render or per keystroke — since
/// each one is a round-trip over the circuit.
/// </summary>
public partial class CarryoverReview
{
    [Inject]
    private IWallUpdateSessionService Sessions { get; set; } = default!;

    /// <summary>
    /// The decisions already recorded on the wall's update session, when this phase is being resumed.
    /// Laid over the carry-all defaults and the matcher's suggestions so a resumed review shows what
    /// the user decided, not what the matcher would propose afresh. Null on a first pass.
    /// </summary>
    [Parameter] public BigUpdateConfirmation? Restored { get; set; }

    /// <summary>
    /// Overlays the persisted verdicts on top of the carry-all seed and the matcher proposals. Order
    /// matters: the user's own decision must win over both, and a persisted decision about a hold that
    /// no longer exists is ignored rather than resurrecting it.
    /// </summary>
    private void SeedFromRestored()
    {
        if (Restored is not { } restored)
        {
            return;
        }

        // A verdict recorded about a hold this pane cannot draw (a co-updated NEIGHBOUR panel's hold,
        // reachable in an older build that drew the whole old generation on the centre photo) is reset
        // to the matcher default before it is laid back: it would otherwise sit in _decisions invisible
        // — not drawn, not counted, in no stepper — and still ship to the promote, where a Removed
        // freezes its boulders. The reset ones are surfaced to the user rather than dropped silently.
        var reconciled = CarryoverScope.Reconcile(Session, restored.Carryover);
        _ownScopeResets = reconciled.Reset;
        foreach (var decision in reconciled.Decisions)
        {
            if (_decisions.ContainsKey(decision.OldHoldId))
            {
                _decisions[decision.OldHoldId] = decision;
            }
        }

        // Only the DISCARDS need restoring: everything else is accepted by default, and the accepted
        // list is re-derived from the current staged set at Continue anyway.
        foreach (var holdId in restored.RemovedNewCenterHoldIds)
        {
            _newDiscarded.Add(holdId);
        }
    }

    // ---- Decision handlers (from the focused stepper) --------------------------
    /// <summary>
    /// The stepper's verdict for one old hold. Anything that arrives here — an accept, a "has
    /// physically changed", a re-target, a removal — is a person deciding, so it is written as
    /// CONFIRMED: the hold then leaves the attention queue and the review lists, and a second admin on
    /// the same session is not asked to look at it again.
    /// </summary>
    private async Task ApplyCarryDecision(CarryDecisionChange change)
    {
        var decision = new CarryoverDecision(change.OldHoldId, change.Kind, change.NewHoldId, Confirmed: true);
        _decisions[change.OldHoldId] = decision;
        await SaveCarryAsync(decision);
    }

    private async Task ApplyNewDecision(NewDecisionChange change)
    {
        if (change.Discarded)
        {
            _newDiscarded.Add(change.NewHoldId);
        }
        else
        {
            _newDiscarded.Remove(change.NewHoldId);
        }

        await SaveNewAsync(change.NewHoldId, change.Discarded);
    }

    /// <summary>
    /// Writes one old hold's verdict through. A failed save is surfaced but never rolled back: the
    /// in-memory decision is what the user sees and what the bulk save at Continue will re-send, so
    /// reverting it here would silently contradict the screen.
    /// </summary>
    private async Task SaveCarryAsync(CarryoverDecision decision)
    {
        try
        {
            await Sessions.SaveCarryDecisionAsync(WallId, decision);
            _saveError = null;
        }
        catch (Exception ex)
        {
            _saveError = $"Could not save that decision yet: {ex.Message}";
        }

        // The queue is a function of the decisions, so it is rebuilt here rather than only at init:
        // a reviewed hold has to leave it (and the headline count) the moment it is decided. Rebuilt
        // even on a failed save, because the in-memory decision is what the screen shows.
        _attentionQueue = BuildAttentionQueue();

        // Only re-read the "reviewed by whom" attribution while it is actually on screen; the reviewed
        // FLAG itself is already in hand on the decision we just wrote.
        if (_showReviewed && decision.Confirmed)
        {
            await LoadConfirmationsAsync();
        }
    }

    private async Task SaveNewAsync(Guid stagedHoldId, bool discarded)
    {
        try
        {
            await Sessions.SaveNewCentreHoldDecisionAsync(WallId, stagedHoldId, discarded);
            _saveError = null;
        }
        catch (Exception ex)
        {
            _saveError = $"Could not save that decision yet: {ex.Message}";
        }
    }

    /// <summary>Writes several verdicts through at once, for the edits that touch more than one hold.</summary>
    private async Task SaveCarryAllAsync(IEnumerable<CarryoverDecision> decisions)
    {
        foreach (var decision in decisions)
        {
            await SaveCarryAsync(decision);
        }
    }

    private string? _saveError;

    // ---- Out-of-scope verdicts (see CarryoverScope) -----------------------------

    /// <summary>
    /// The verdicts the WIZARD already reset on this session, so the notice appears on the step where
    /// the holds would have been reviewed and not only on the confirm step. The wizard resets them the
    /// moment it restores a session, which is why this step rarely finds any of its own.
    /// </summary>
    [Parameter] public IReadOnlyList<CarryoverDecision> ScopeResets { get; set; } = [];

    /// <summary>Raised when the user acknowledges the notice, so the wizard stops holding the promote.</summary>
    [Parameter] public EventCallback OnScopeResetsAcknowledged { get; set; }

    /// <summary>
    /// Verdicts THIS step reset while seeding. Normally empty (the wizard got there first); kept because
    /// the seed must reconcile whatever it is handed rather than trust that someone else already did.
    /// </summary>
    private IReadOnlyList<CarryoverDecision> _ownScopeResets = [];

    private bool _scopeResetsAcknowledged;

    private int ScopeResetCount => ScopeResets
        .Concat(_ownScopeResets)
        .Select(d => d.OldHoldId)
        .Distinct()
        .Count();

    private bool ScopeResetsPending => ScopeResetCount > 0 && !_scopeResetsAcknowledged;

    private async Task AcknowledgeScopeResetsAsync()
    {
        _scopeResetsAcknowledged = true;
        await OnScopeResetsAcknowledged.InvokeAsync();
    }
}
