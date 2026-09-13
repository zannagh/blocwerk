using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Phase 1 of the big-wall update: the carryover of the old, curated holds onto the new centre photo.
/// Shows the old (left) and new (right) photos colour-coded, a live summary, and focused review
/// steppers for the decisions that matter. EVERY old hold is carried by default; the matcher only
/// overlays mapping suggestions, so a matcher miss can never silently destroy a boulder. The new
/// image is directly editable (add / move / resize / remove staged holds) to fix detection errors.
/// State lives here; markup is in the .razor.
/// </summary>
public partial class CarryoverReview
{
    [Inject]
    private IWallService WallService { get; set; } = default!;

    [Inject]
    private IWallPanelService WallPanelService { get; set; } = default!;

    [Parameter] public Guid WallId { get; set; }
    [Parameter] public int CurrentGeneration { get; set; }
    [Parameter] public BigUpdateSession Session { get; set; } = default!;
    [Parameter] public EventCallback<CarryoverOutcome> OnContinue { get; set; }
    [Parameter] public EventCallback OnDiscard { get; set; }

    private const string Green = "var(--status-success)";
    private const string Amber = "var(--status-warning)";
    private const string Red = "var(--status-danger)";
    private const string Blue = "var(--status-info)";
    private const string Grey = "rgba(150,150,160,0.5)";

    // Normalized default radius for a user-added hold on the new image (~2% of the panel).
    private const double DefaultNewHoldRadius = 0.02;

    private bool _loading = true;
    private CarryReviewMode? _reviewMode;

    // Right-pane (new image) editing state.
    private bool _addMode;
    private Guid? _selectedNewHoldId;

    // Every old live hold starts carried in place (default-KEEP); the new-centre holds are all
    // accepted unless the user explicitly discards one.
    private readonly Dictionary<Guid, CarryoverDecision> _decisions = [];
    private readonly HashSet<Guid> _newDiscarded = [];

    private List<PanelHold> _oldHolds = [];
    private List<PanelHold> _newHolds = [];

    private Guid CenterPanelId => Session.CenterPanelId;

    private bool AutoMatchDegraded => Session.AutoMatchStatus != AutoMatchStatus.Ok;

    protected override async Task OnInitializedAsync()
    {
        var old = await WallService.GetHoldsForGenerationAsync(WallId, CurrentGeneration);
        _oldHolds = old.Select(h => new PanelHold(h.Id, h.X, h.Y, h.Radius, h.Color)).ToList();
        await ReloadNewHoldsAsync();

        // Carry-all default: EVERY old hold gets a Carried, no-twin decision. This is where the
        // "never silently lose a hold" invariant lives — even a total matcher failure leaves every
        // old hold carried, so nothing is ever dropped.
        foreach (var h in _oldHolds)
        {
            _decisions[h.Id] = new CarryoverDecision(h.Id, CarryKind.Carried, null);
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
    private int CarryingCount => _decisions.Values.Count(d => d.Kind == CarryKind.Carried);
    private int ChangedCount => _decisions.Values.Count(d => d.Kind == CarryKind.Changed);
    private int RemovingCount => _decisions.Values.Count(d => d.Kind == CarryKind.Removed);

    // Staged holds that no old hold consumed as a twin — the genuinely new holds (user additions
    // included, deletions naturally excluded since they leave _newHolds).
    private HashSet<Guid> ConsumedNewIds =>
        _decisions.Values.Where(d => d.NewHoldId is not null).Select(d => d.NewHoldId!.Value).ToHashSet();

    private int NewCount =>
        _newHolds.Count(h => !_newDiscarded.Contains(h.Id) && !ConsumedNewIds.Contains(h.Id));

    // The carried-holds review queue walks EVERY carried hold so the user can toggle "physically
    // changed" on any of them — deterministic and matcher-independent (the matcher no longer flags
    // moves). The button is labelled "Review carried" because this is the count it walks; the amber
    // "changed" chip reports how many of them are flagged, via ChangedCount above.
    private int CarriedToReview => _decisions.Values.Count(d => d.Kind is CarryKind.Carried or CarryKind.Changed);
    private int RemovalToReview => Session.RemovedCandidateHoldIds.Count;
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
        _decisions.Where(kv => kv.Value.Kind is CarryKind.Carried or CarryKind.Changed)
            .Select(kv => new CarryReviewItem(kv.Key, kv.Value.NewHoldId, kv.Value.Kind)).ToList();

    private List<CarryReviewItem> RemovalItems =>
        Session.RemovedCandidateHoldIds.Select(ItemForOld).ToList();

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
        foreach (var d in _decisions.Values)
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

        foreach (var d in _decisions.Values)
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

    // ---- Right-pane (new image) staged edits -----------------------------------
    private void SelectNewHold(Guid id) => _selectedNewHoldId = id;

    private async Task OnNewHoldGeometryChanged(PanelImageView.HoldGeometry g)
    {
        await WallPanelService.UpdateStagedHoldAsync(WallId, g.HoldId, g.X, g.Y, g.Radius);
        await ReloadNewHoldsAsync();
    }

    private async Task OnNewEmptyTap((double X, double Y) at)
    {
        var id = await WallPanelService.AddStagedHoldAsync(WallId, CenterPanelId, at.X, at.Y, DefaultNewHoldRadius);
        _addMode = false;
        _selectedNewHoldId = id;
        await ReloadNewHoldsAsync();
    }

    private async Task RemoveSelectedNewHold()
    {
        if (_selectedNewHoldId is not { } id)
        {
            return;
        }

        await WallPanelService.DeleteStagedHoldAsync(WallId, id);

        // Any carry decision that pointed at this now-deleted staged twin falls back to a carry in
        // place (no twin), so no decision references a hold that no longer exists.
        foreach (var (oldId, d) in _decisions.Where(kv => kv.Value.NewHoldId == id).ToList())
        {
            _decisions[oldId] = d with { NewHoldId = null };
        }

        _selectedNewHoldId = null;
        await ReloadNewHoldsAsync();
    }

    private void ToggleAddMode()
    {
        _addMode = !_addMode;
        if (_addMode)
        {
            _selectedNewHoldId = null;
        }
    }

    // ---- Decision handlers (from the focused stepper) --------------------------
    private void ApplyCarryDecision(CarryDecisionChange change) =>
        _decisions[change.OldHoldId] = new CarryoverDecision(change.OldHoldId, change.Kind, change.NewHoldId);

    private void ApplyNewDecision(NewDecisionChange change)
    {
        if (change.Discarded)
        {
            _newDiscarded.Add(change.NewHoldId);
        }
        else
        {
            _newDiscarded.Remove(change.NewHoldId);
        }
    }

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
