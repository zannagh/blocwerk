// <copyright file="CarryoverReview.Relocations.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The "Possibly moved" suggestions of this review (see <see cref="RelocationSuggestions"/>). The session
/// service owns the verdict; this partial mirrors it into <see cref="_decisions"/> so the counts, colours,
/// queues and the bulk save at Continue all see an accepted answer as what it is — the old hold carried
/// onto the new detection as CHANGED (moved) or CARRIED (same hold) — and never overwrite it with a stale
/// in-memory default.
/// </summary>
public partial class CarryoverReview
{
    private IReadOnlyList<RelocationSuggestion> relocations = [];
    private bool relocationBusy;

    // The photo panes the "Show" of a pair re-centres; scrolled into view because on a phone they sit below the list.
    private ElementReference relocationPanes;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    // The pair last shown on the photos: both panes highlight their hold with the row's number badge.
    private RelocationRow? relocationFocus;
    private int relocationFocusKey;

    private string NewPhotoUrl => $"/api/walls/{WallId}/panels/{CenterPanelId}/staged-photo";

    /// <summary>
    /// The suggestions this pane can draw — both holds on the displayed panel — numbered best first.
    /// A suggestion whose new hold another old hold now claims stays listed but cannot be accepted.
    /// </summary>
    private List<RelocationRow> RelocationRows
    {
        get
        {
            var oldById = _oldHolds.ToDictionary(h => h.Id);
            var newById = _newHolds.ToDictionary(h => h.Id);
            var rows = new List<RelocationRow>();
            foreach (var s in relocations)
            {
                if (!oldById.TryGetValue(s.OldHoldId, out var oldHold) || !newById.TryGetValue(s.NewHoldId, out var newHold))
                {
                    continue;
                }

                var claimed = DisplayedDecisions.Any(d =>
                    d.OldHoldId != s.OldHoldId && d.Kind != CarryKind.Removed && d.NewHoldId == s.NewHoldId);
                rows.Add(new RelocationRow(
                    s, rows.Count + 1, oldHold, newHold, HoldLabel(s.OldHoldId), _boulderHoldIds.Contains(s.OldHoldId), claimed));
            }

            return rows;
        }
    }

    private Guid? OldHighlightId => relocationFocus?.OldHold.Id;

    private Guid? NewHighlightId => relocationFocus?.NewHold.Id;

    private int RelocationBadge => relocationFocus?.Number ?? 0;

    private (double X, double Y)? OldFocusPoint =>
        relocationFocus is { OldHold: var h } ? (h.X, h.Y) : null;

    private (double X, double Y)? NewFocusPoint =>
        relocationFocus is { NewHold: var h } ? (h.X, h.Y) : null;

    private async Task LoadRelocationsAsync()
    {
        try
        {
            relocations = await Sessions.GetRelocationSuggestionsAsync(WallId);
        }
        catch (Exception ex)
        {
            // Suggestions are optional help: failing to read them must never block the review.
            relocations = [];
            _saveError = $"Could not load the possibly-moved holds: {ex.Message}";
        }
    }

    private async Task ShowRelocation(RelocationRow row)
    {
        relocationFocus = row;
        relocationFocusKey++;
        try
        {
            await JS.InvokeVoidAsync("wallEditor.revealElement", relocationPanes);
        }
        catch (JSException)
        {
            // Scrolling is a convenience: the panes are re-centred either way.
        }
        catch (JSDisconnectedException)
        {
            // Circuit gone; nothing to scroll.
        }
    }

    private async Task DecideRelocationAsync((RelocationSuggestion Suggestion, RelocationDecision Decision) choice)
    {
        var (suggestion, decision) = choice;
        relocationBusy = true;
        try
        {
            await Sessions.DecideRelocationAsync(WallId, suggestion.Id, decision);
            MirrorRelocationVerdict(suggestion, decision);
            _saveError = null;
        }
        catch (Exception ex)
        {
            _saveError = $"Could not save that decision yet: {ex.Message}";
        }
        finally
        {
            relocationBusy = false;
        }

        await LoadRelocationsAsync();
        _attentionQueue = BuildAttentionQueue();
    }

    /// <summary>
    /// The in-memory twin of what the service just wrote: "moved here" = a confirmed Changed carry onto the
    /// new hold, "same hold" = a confirmed Carried one; undoing either = back to the unconfirmed
    /// carried-in-place default, but only while the verdict is still that accept's (a later hand-made
    /// verdict is left alone, as the service does).
    /// </summary>
    private void MirrorRelocationVerdict(RelocationSuggestion suggestion, RelocationDecision decision)
    {
        if (decision != RelocationDecision.Dismiss)
        {
            var kind = decision == RelocationDecision.SameHold ? CarryKind.Carried : CarryKind.Changed;
            _decisions[suggestion.OldHoldId] =
                new CarryoverDecision(suggestion.OldHoldId, kind, suggestion.NewHoldId, Confirmed: true);
            return;
        }

        if (RelocationFold.VerdictOf(suggestion.Status) is { } written
            && _decisions.GetValueOrDefault(suggestion.OldHoldId) is { } current
            && current.Kind == written
            && current.NewHoldId == suggestion.NewHoldId)
        {
            _decisions[suggestion.OldHoldId] = new CarryoverDecision(suggestion.OldHoldId, CarryKind.Carried, null);
        }
    }
}
