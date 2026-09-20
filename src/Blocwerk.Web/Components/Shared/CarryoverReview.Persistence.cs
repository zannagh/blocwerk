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

        foreach (var decision in restored.Carryover)
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
    private async Task ApplyCarryDecision(CarryDecisionChange change)
    {
        var decision = new CarryoverDecision(change.OldHoldId, change.Kind, change.NewHoldId);
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
}
