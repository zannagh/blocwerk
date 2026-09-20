using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Phase 1 of the big-wall update: the carryover of the old, curated holds onto the new centre photo.
/// Shows the old (left) and new (right) photos colour-coded, a live summary, and focused review
/// steppers for the decisions that matter. EVERY old hold is carried by default; the matcher only
/// overlays mapping suggestions, so a matcher miss can never silently destroy a boulder. The new
/// image is directly editable (add / move / resize / remove staged holds) to fix detection errors —
/// that right-pane editing lives in CarryoverReview.Tools.cs. State lives here; markup in the .razor.
/// </summary>
public partial class CarryoverReview
{
    [Inject]
    private IWallPanelService WallPanelService { get; set; } = default!;

    [Parameter] public Guid WallId { get; set; }
    [Parameter] public BigUpdateSession Session { get; set; } = default!;
    [Parameter] public EventCallback<CarryoverOutcome> OnContinue { get; set; }
    [Parameter] public EventCallback OnDiscard { get; set; }

    /// <summary>
    /// Raised when a sub-view (the focused stepper or the cross-gen link tool) opens or closes, so the
    /// wizard can drop its own key bindings while another surface owns the screen.
    /// </summary>
    [Parameter] public EventCallback<bool> OnSubViewOpenChanged { get; set; }

    private const string Green = "var(--status-success)";
    private const string Amber = "var(--status-warning)";
    private const string Red = "var(--status-danger)";
    private const string Blue = "var(--status-info)";
    private const string Grey = "rgba(150,150,160,0.5)";

    private bool _loading = true;
    private CarryReviewMode? _reviewMode;

    // Every old live hold starts carried in place (default-KEEP); the new-centre holds are all
    // accepted unless the user explicitly discards one.
    private readonly Dictionary<Guid, CarryoverDecision> _decisions = [];
    private readonly HashSet<Guid> _newDiscarded = [];

    // The re-photographed panel this review DISPLAYS, and the old holds carried on it. Hold coordinates
    // are PANEL-normalized, so a pane may only draw the old set of the panel whose photo it is showing:
    // drawing the wall's whole old generation here put every side-panel hold on the centre image as a
    // circle floating over the mats, and a Removed verdict on one of those phantoms froze real boulders.
    // The session supplies the set per panel (Session.CarriedPanels) so this can never drift from the
    // matcher. It is deliberately STATE rather than a hardcoded centre lookup: a panel selector (as the
    // touch-up step already has) only needs to reassign _displayedPanel and the "before" photo URL.
    private CarriedPanelOldHolds? _displayedPanel;

    private List<PanelHold> _oldHolds = [];
    private List<PanelHold> _newHolds = [];

    // The ids of _oldHolds, for scoping every UI derivation off the all-panel decision map below.
    private HashSet<Guid> _displayedOldIds = [];

    private Guid CenterPanelId => Session.CenterPanelId;

    // The "before" image: the LIVE panel of whichever panel is displayed, so the old-hold overlay and the
    // photo underneath it always describe the same panel. Falls back to the wall photo route (which serves
    // the live centre panel, with its own legacy fallback) when the session predates the per-panel shape.
    private string OldPhotoUrl => _displayedPanel?.LivePanelId is { } livePanelId
        ? $"/api/walls/{WallId}/panels/{livePanelId}/photo"
        : $"/api/walls/{WallId}/photo";

    private bool AutoMatchDegraded => Session.AutoMatchStatus != AutoMatchStatus.Ok;

    protected override async Task OnInitializedAsync()
    {
        // Today the review displays the CENTRE panel: its "before" image is /api/walls/{id}/photo, which
        // serves the live centre panel, and its editable "after" pane is the staged centre. The other
        // re-photographed panels are carried and matched all the same (the service matches each panel's
        // old holds against its OWN staged detections) — they are simply not drawn here.
        _displayedPanel = (Session.CarriedPanels ?? []).FirstOrDefault(p => p is { Col: 0, Row: 0 });
        _oldHolds = (_displayedPanel?.OldHolds ?? []).ToList();
        _displayedOldIds = _oldHolds.Select(h => h.Id).ToHashSet();
        await ReloadNewHoldsAsync();

        // Carry-all default: EVERY carried old hold gets a Carried, no-twin decision. This is where the
        // "never silently lose a hold" invariant lives — even a total matcher failure leaves every
        // old hold carried, so nothing is ever dropped. Deliberately seeded over the session's FULL
        // carried set (centre AND every co-updated neighbour panel), not just the drawn centre set: the
        // promote's reconcile would otherwise default an undecided neighbour hold to a twin-less carry
        // and clone it forward alongside the staged detection it should have promoted in place.
        // Only the displayed panel's subset is ever drawn or stepped through — see _displayedOldIds.
        foreach (var id in Session.CarriedOldHoldIds ?? [])
        {
            _decisions[id] = new CarryoverDecision(id, CarryKind.Carried, null);
        }

        // Overlay the matcher's proposals as SUGGESTIONS only: set the new-centre twin where a
        // proposal exists. Never auto-set Changed — "physically changed" is a 100% manual assertion.
        // If the session carries no proposals (matcher unavailable/failed), the flow still works
        // fully because everything above is already carried.
        foreach (var p in Session.Carryover)
        {
            if (_decisions.ContainsKey(p.OldHoldId))
            {
                _decisions[p.OldHoldId] = new CarryoverDecision(p.OldHoldId, CarryKind.Carried, p.NewHoldId);
            }
        }

        // Finally, lay back anything already decided on the session (a resumed update): the user's own
        // verdicts outrank both the carry-all seed and the matcher's suggestions. See the Persistence
        // partial.
        SeedFromRestored();

        // Derive the FEW old holds that actually need the user's eyes (no proposal / low confidence /
        // high residual), residual-ranked. Static per session, so compute once here.
        _attentionQueue = BuildAttentionQueue();

        _loading = false;
    }

    // Re-query the staged centre holds so the overview overlay + counts track any staged edit.
    private async Task ReloadNewHoldsAsync()
    {
        _newHolds = (await WallPanelService.GetPanelHoldsAsync(WallId, CenterPanelId, includeStaged: true)).ToList();
    }

    // ---- Live summary ----------------------------------------------------------
    // Every count and list below reads DisplayedDecisions, never _decisions: the pane speaks about the
    // panel it draws. Holds on the other re-photographed panels keep their carry decision (so the
    // promote still twins them in place) but they are NOT reviewable anywhere: _displayedPanel is
    // pinned to the centre (0,0) and there is no panel selector yet, so their decision can only ever be
    // the matcher default. That is a real capability gap — a neighbour panel's old hold cannot be
    // marked Removed at all — whose fix is the panel selector, deliberately a separate change.
    // CarryoverScope is what keeps a verdict recorded on one of them (by an older build that drew them
    // all on this photo) from taking effect where nobody can see it.
    private IEnumerable<CarryoverDecision> DisplayedDecisions =>
        _decisions.Values.Where(d => _displayedOldIds.Contains(d.OldHoldId));

    private int CarryingCount => DisplayedDecisions.Count(d => d.Kind == CarryKind.Carried);
    private int ChangedCount => DisplayedDecisions.Count(d => d.Kind == CarryKind.Changed);
    private int RemovingCount => DisplayedDecisions.Count(d => d.Kind == CarryKind.Removed);

    // Staged holds that no old hold consumed as a twin — the genuinely new holds (user additions
    // included, deletions naturally excluded since they leave _newHolds).
    private HashSet<Guid> ConsumedNewIds =>
        DisplayedDecisions.Where(d => d.NewHoldId is not null).Select(d => d.NewHoldId!.Value).ToHashSet();

    private int NewCount =>
        _newHolds.Count(h => !_newDiscarded.Contains(h.Id) && !ConsumedNewIds.Contains(h.Id));

    // The carried-holds review queue walks EVERY carried hold so the user can toggle "physically
    // changed" on any of them — deterministic and matcher-independent (the matcher no longer flags
    // moves). The button is labelled "Review carried" because this is the count it walks; the amber
    // "changed" chip reports how many of them are flagged, via ChangedCount above.
    private int CarriedToReview => DisplayedDecisions.Count(d => d.Kind is CarryKind.Carried or CarryKind.Changed);
    private int RemovalToReview => DisplayedRemovedCandidates.Count;
    private int NewToReview => Session.NewCenterHoldIds.Count(id => _newHolds.Any(h => h.Id == id));

    // ---- Review item lists (walked one at a time) ------------------------------
    // Every item carries the PERSISTED decision (Kind + NewHoldId) so the stepper can reflect prior
    // state on reopen instead of resetting to a blank "carried". _decisions is the source of truth.
    private CarryReviewItem ItemForOld(Guid oldId)
    {
        var d = _decisions.GetValueOrDefault(oldId);
        return new CarryReviewItem(oldId, d?.NewHoldId, d?.Kind ?? CarryKind.Carried);
    }

    private List<CarryReviewItem> CarriedItems =>
        DisplayedDecisions.Where(d => d.Kind is CarryKind.Carried or CarryKind.Changed)
            .Select(d => new CarryReviewItem(d.OldHoldId, d.NewHoldId, d.Kind)).ToList();

    // The matcher reports removal candidates for EVERY re-photographed panel (its per-panel carryover
    // pass). Only the displayed panel's can be drawn over this photo, so only those are offered here.
    private List<Guid> DisplayedRemovedCandidates =>
        Session.RemovedCandidateHoldIds.Where(_displayedOldIds.Contains).ToList();

    private List<CarryReviewItem> RemovalItems =>
        DisplayedRemovedCandidates.Select(ItemForOld).ToList();

    private List<CarryReviewItem> NewItems =>
        Session.NewCenterHoldIds.Where(id => _newHolds.Any(h => h.Id == id))
            .Select(id => new CarryReviewItem(null, id)).ToList();

    private List<CarryReviewItem> ReviewItems => _reviewMode switch
    {
        CarryReviewMode.Uncertain => UncertainItems,
        CarryReviewMode.Carried => CarriedItems,
        CarryReviewMode.Removal => RemovalItems,
        CarryReviewMode.New => NewItems,
        _ => [],
    };

    // ---- Colour coding ---------------------------------------------------------
    private Dictionary<Guid, string> OldColors()
    {
        var map = new Dictionary<Guid, string>();
        foreach (var d in DisplayedDecisions)
        {
            map[d.OldHoldId] = d.Kind switch
            {
                CarryKind.Changed => Amber,
                CarryKind.Removed => Red,
                // Carried with a match = a clean carry; carried without one = kept blind at the old
                // position (matcher couldn't re-find it, but it is NOT removed — still safe).
                _ => d.NewHoldId is not null ? Green : Amber,
            };
        }

        return map;
    }

    private Dictionary<Guid, string> NewColors()
    {
        var map = new Dictionary<Guid, string>();
        var consumed = ConsumedNewIds;

        foreach (var d in DisplayedDecisions)
        {
            if (d.NewHoldId is { } nid)
            {
                map[nid] = d.Kind == CarryKind.Changed ? Amber : Green;
            }
        }

        // Every remaining staged hold (including user-added ones) is a new hold unless discarded.
        foreach (var h in _newHolds)
        {
            if (consumed.Contains(h.Id))
            {
                continue;
            }

            map[h.Id] = _newDiscarded.Contains(h.Id) ? Grey : Blue;
        }

        return map;
    }

    // The decision handlers and the as-you-go saves live in CarryoverReview.Persistence.cs.
    private void OpenReview(CarryReviewMode mode) => _reviewMode = mode;

    private void CloseReview() => _reviewMode = null;

    private async Task Continue()
    {
        // A new-centre hold consumed by a carryover match must not also be kept as a standalone new
        // hold. Derive accepted/removed from the CURRENT staged set so user additions are included
        // and deletions are naturally excluded.
        var consumed = ConsumedNewIds;

        var accepted = _newHolds.Select(h => h.Id)
            .Where(id => !_newDiscarded.Contains(id) && !consumed.Contains(id))
            .ToList();
        var removed = _newHolds.Select(h => h.Id)
            .Where(id => _newDiscarded.Contains(id) && !consumed.Contains(id))
            .ToList();

        await OnContinue.InvokeAsync(new CarryoverOutcome(_decisions.Values.ToList(), accepted, removed));
    }
}
