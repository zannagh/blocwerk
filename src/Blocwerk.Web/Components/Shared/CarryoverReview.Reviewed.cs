// <copyright file="CarryoverReview.Reviewed.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The REVIEWED half of the carryover review: which old holds a human has already signed off, who did
/// it, and the toggle that lists them so a sign-off can be undone.
/// <para>
/// A confirmed hold drops out of the attention queue and out of every review list, so the counts on the
/// overview shrink as the work is done and a second admin working the same session is not asked to
/// re-review what the first already decided. The confirmation is review metadata only — the verdict it
/// was made about is unchanged, and the promote never reads it.
/// </para>
/// <para>
/// The session is per WALL and shared, but this component reads it ONCE (OnInitializedAsync) and there
/// is no push or poll: admin B sees admin A's confirmations on load or refresh, not live.
/// </para>
/// </summary>
public partial class CarryoverReview
{
    // Attribution for the confirmed holds — who signed each one off, and when. Only fetched when it is
    // actually displayed (the boolean alone already rides on every decision), so it is loaded at init
    // and refreshed whenever the reviewed list is on screen.
    private Dictionary<Guid, CarryConfirmation> _confirmations = [];

    // The escape hatch: list the holds somebody has already reviewed, so a mistake can be found and
    // un-reviewed. OFF by default — the whole point of the filter is that reviewed work stays done.
    // It reveals the LIST only; it never puts a reviewed hold back into a queue or into a count.
    private bool _showReviewed;

    /// <summary>True when a human has signed this old hold's verdict off.</summary>
    private bool IsReviewed(Guid oldHoldId) =>
        _decisions.TryGetValue(oldHoldId, out var d) && d.Confirmed;

    /// <summary>
    /// Every reviewed hold the un-review button has to be able to reach: the displayed panel's first, in
    /// the order the panel lists them, then any sign-off recorded against a hold this pane cannot draw.
    /// <para>
    /// Those out-of-panel ones are real — a co-updated NEIGHBOUR panel's hold can hold a persisted
    /// confirmation, laid back by <c>SeedFromRestored</c> (<see cref="CarryoverScope"/> only resets what
    /// is outside the SESSION, not what is outside this panel). Listing only <c>_oldHolds</c> made them
    /// unreachable: confirmed, hidden from every queue by <see cref="ExcludeReviewed"/>, and with no way
    /// to un-review them. Reading the whole decision map is the deliberate choice over narrowing the
    /// writes that can create one, because those writes exist for a different reason — no decision may
    /// point at a staged hold that was just deleted — and narrowing them would leave a dangling twin.
    /// </para>
    /// </summary>
    private List<Guid> ReviewedOldIds
    {
        get
        {
            var displayed = _oldHolds.Select(h => h.Id).Where(IsReviewed).ToList();
            var shown = displayed.ToHashSet();
            var elsewhere = _decisions.Values
                .Where(d => d.Confirmed && !shown.Contains(d.OldHoldId))
                .Select(d => d.OldHoldId)
                .OrderBy(id => id);
            return [.. displayed, .. elsewhere];
        }
    }

    /// <summary>
    /// The headline "already reviewed" figure. DISPLAYED panel only, because it is subtracted from this
    /// panel's total in <c>AutoCarriedCount</c> — an out-of-panel sign-off would deflate that count for
    /// holds it never counted in the first place. The list above is deliberately wider than this number.
    /// </summary>
    private int ReviewedCount => _oldHolds.Count(h => IsReviewed(h.Id));

    /// <summary>
    /// How many rows the reviewed LIST holds — this panel's plus any out-of-panel sign-off. Drives the
    /// toggle's label and whether the block is offered at all, so a confirmation recorded on a
    /// co-updated neighbour panel is always reachable even when this panel has none of its own.
    /// </summary>
    private int ReviewedListCount => ReviewedOldIds.Count;

    /// <summary>
    /// Drops the reviewed holds from a queue. The one filter every review list runs through, so the
    /// lists, the attention queue and the counts can never disagree.
    /// <para>
    /// It is NOT conditioned on the show-reviewed toggle. That toggle shows the reviewed list below the
    /// headline; letting it also un-filter the queues made it change the COUNTS — reviewed holds
    /// re-entered <c>_attentionQueue</c>, so <c>NeedsReviewCount</c> re-inflated and
    /// <c>AutoCarriedCount</c> subtracted them twice, usually clamping to 0 — and froze a stepper opened
    /// while it was on into a walk full of already-signed-off holds. The way back into a queue is
    /// <see cref="UnReviewAsync"/>, which is what the list's own button does.
    /// </para>
    /// </summary>
    private IEnumerable<T> ExcludeReviewed<T>(IEnumerable<T> items, Func<T, Guid?> holdIdOf) =>
        items.Where(item => holdIdOf(item) is not { } id || !IsReviewed(id));

    /// <summary>A stable, human-readable handle for a hold: its position in the panel's old-hold list.</summary>
    private string HoldLabel(Guid oldHoldId)
    {
        var index = _oldHolds.FindIndex(h => h.Id == oldHoldId);
        return index >= 0 ? $"Hold {index + 1}" : "Hold on another panel";
    }

    /// <summary>Who reviewed this hold and when — "by whom" unknown is said out loud, never guessed.</summary>
    private string ReviewedLabel(Guid oldHoldId)
    {
        if (!_confirmations.TryGetValue(oldHoldId, out var c))
        {
            return "Reviewed";
        }

        var who = string.IsNullOrWhiteSpace(c.ConfirmedByName) ? "unknown admin" : c.ConfirmedByName;
        return $"Reviewed by {who} · {c.ConfirmedAt.ToLocalTime():d MMM, HH:mm}";
    }

    /// <summary>The stepper's reviewed note for one hold: null unless somebody has reviewed it.</summary>
    private string? ReviewedNoteFor(Guid oldHoldId) =>
        IsReviewed(oldHoldId) ? ReviewedLabel(oldHoldId) : null;

    // Display only: the queues and every count are unaffected — see ExcludeReviewed.
    private async Task ToggleShowReviewedAsync()
    {
        _showReviewed = !_showReviewed;
        if (_showReviewed)
        {
            await LoadConfirmationsAsync();
        }
    }

    /// <summary>
    /// Un-reviews one hold: the sign-off goes, the verdict stays exactly as it was. The recovery path
    /// for a mis-click, and the only way to clear a confirmation without changing the decision.
    /// </summary>
    private async Task UnReviewAsync(Guid oldHoldId)
    {
        try
        {
            await Sessions.ClearCarryConfirmationAsync(WallId, oldHoldId);
            _saveError = null;
        }
        catch (Exception ex)
        {
            _saveError = $"Could not un-review that hold yet: {ex.Message}";
            return;
        }

        if (_decisions.TryGetValue(oldHoldId, out var d))
        {
            _decisions[oldHoldId] = d with { Confirmed = false };
        }

        _confirmations.Remove(oldHoldId);
        _attentionQueue = BuildAttentionQueue();
    }

    /// <summary>
    /// Reads the wall session's confirmation attribution. Failure is silent by design: the reviewed
    /// FLAG rides on the decisions themselves, so losing the "by whom" only costs a name on screen.
    /// </summary>
    private async Task LoadConfirmationsAsync()
    {
        try
        {
            var rows = await Sessions.GetCarryConfirmationsAsync(WallId);
            _confirmations = rows.ToDictionary(c => c.OldHoldId);
        }
        catch (Exception)
        {
            _confirmations = [];
        }
    }
}
