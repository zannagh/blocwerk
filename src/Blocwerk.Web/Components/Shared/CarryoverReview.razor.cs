using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Phase 1 of the big-wall update: the carryover of the old, curated holds onto the new centre photo.
/// Shows the old (left) and new (right) photos colour-coded, a live summary, and focused review
/// steppers for the decisions that matter. Every proposal defaults to KEEP — the clear carries and
/// new holds are accepted on Continue unless the user explicitly removes/discards them, so a matcher
/// miss can never silently destroy a boulder. State lives here; markup is in the .razor.
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

    private const string Green = "#33cc66";
    private const string Amber = "#ffb020";
    private const string Red = "#ff5555";
    private const string Blue = "#4aa8ff";
    private const string Grey = "rgba(150,150,160,0.5)";

    private bool _loading = true;
    private CarryReviewMode? _reviewMode;

    // Every old live hold starts with a KEEP-leaning default; the new-centre holds are all accepted
    // unless the user explicitly discards one.
    private readonly Dictionary<Guid, CarryoverDecision> _decisions = [];
    private readonly HashSet<Guid> _newDiscarded = [];

    private List<PanelHold> _oldHolds = [];
    private List<PanelHold> _newHolds = [];

    private Guid CenterPanelId => Session.CenterPanelId;

    protected override async Task OnInitializedAsync()
    {
        var old = await WallService.GetHoldsForGenerationAsync(WallId, CurrentGeneration);
        _oldHolds = old.Select(h => new PanelHold(h.Id, h.X, h.Y, h.Radius, h.Color)).ToList();
        _newHolds = (await WallPanelService.GetPanelHoldsAsync(WallId, CenterPanelId, includeStaged: true)).ToList();

        foreach (var p in Session.Carryover)
        {
            _decisions[p.OldHoldId] = new CarryoverDecision(
                p.OldHoldId, p.Moved ? CarryKind.Moved : CarryKind.Carried, p.NewHoldId);
        }

        foreach (var id in Session.RemovedCandidateHoldIds)
        {
            // Default KEEP at the old position — the matcher just couldn't re-find it.
            _decisions[id] = new CarryoverDecision(id, CarryKind.Carried, null);
        }

        _loading = false;
    }

    // ---- Live summary ----------------------------------------------------------
    private int CarryingCount => _decisions.Values.Count(d => d.Kind == CarryKind.Carried);
    private int MovedCount => _decisions.Values.Count(d => d.Kind == CarryKind.Moved);
    private int RemovingCount => _decisions.Values.Count(d => d.Kind == CarryKind.Removed);
    private int NewCount => Session.NewCenterHoldIds.Count(id => !_newDiscarded.Contains(id));

    private int MovedToReview => Session.Carryover.Count(p => p.Moved);
    private int RemovalToReview => Session.RemovedCandidateHoldIds.Count;
    private int NewToReview => Session.NewCenterHoldIds.Count;

    // ---- Review item lists (walked one at a time) ------------------------------
    private List<CarryReviewItem> MovedItems =>
        Session.Carryover.Where(p => p.Moved)
            .Select(p => new CarryReviewItem(p.OldHoldId, p.NewHoldId)).ToList();

    private List<CarryReviewItem> RemovalItems =>
        Session.RemovedCandidateHoldIds.Select(id => new CarryReviewItem(id, null)).ToList();

    private List<CarryReviewItem> NewItems =>
        Session.NewCenterHoldIds.Select(id => new CarryReviewItem(null, id)).ToList();

    private List<CarryReviewItem> ReviewItems => _reviewMode switch
    {
        CarryReviewMode.Moved => MovedItems,
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
                CarryKind.Moved => Amber,
                CarryKind.Removed => Red,
                // Kept with a match = a clean carry; kept without one = a removal candidate we held on to.
                _ => d.NewHoldId is not null ? Green : Red,
            };
        }

        return map;
    }

    private Dictionary<Guid, string> NewColors()
    {
        var map = new Dictionary<Guid, string>();
        foreach (var d in _decisions.Values)
        {
            if (d.NewHoldId is { } nid)
            {
                map[nid] = d.Kind == CarryKind.Moved ? Amber : Green;
            }
        }

        foreach (var id in Session.NewCenterHoldIds)
        {
            map[id] = _newDiscarded.Contains(id) ? Grey : Blue;
        }

        return map;
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
        // A new-centre hold consumed by a carryover match must not also be kept as a standalone new hold.
        var consumed = _decisions.Values
            .Where(d => d.NewHoldId is not null)
            .Select(d => d.NewHoldId!.Value)
            .ToHashSet();

        var accepted = Session.NewCenterHoldIds
            .Where(id => !_newDiscarded.Contains(id) && !consumed.Contains(id))
            .ToList();
        var removed = Session.NewCenterHoldIds.Where(id => _newDiscarded.Contains(id)).ToList();

        await OnContinue.InvokeAsync(new CarryoverOutcome(_decisions.Values.ToList(), accepted, removed));
    }
}
