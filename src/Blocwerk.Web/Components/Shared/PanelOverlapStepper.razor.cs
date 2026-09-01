using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// One-at-a-time overlap confirmation stepper for a newly staged big-wall panel. State and
/// navigation live here; the markup (two <see cref="PanelImageView"/>s, action bar, legend)
/// is in PanelOverlapStepper.razor.
/// </summary>
public partial class PanelOverlapStepper
{
    [Parameter] public Guid WallId { get; set; }
    [Parameter] public Guid PanelId { get; set; }
    [Parameter] public int Col { get; set; }
    [Parameter] public int Row { get; set; }
    [Parameter] public IReadOnlyList<OverlapProposalDto> Proposals { get; set; } = [];
    [Parameter] public EventCallback<PanelConfirmation> OnConfirm { get; set; }
    [Parameter] public EventCallback OnDiscard { get; set; }

    private const double HighConfidence = 0.90;

    private bool _loading = true;
    private bool _finishing;
    private int _index;
    private List<OverlapProposalDto> _steps = [];
    private ConfirmedLink?[] _decisions = [];

    // Neighbour holds the user marked as physically removed. Accumulated, never applied mid-flow:
    // deletions go in atomically with the links at the final Confirm, and not at all on Discard.
    private readonly HashSet<Guid> _removed = [];
    private string? _warning;

    private bool _movedMode;
    private bool _addMode;
    private Guid? _movedSelectedHoldId;
    private bool _refocus = true;

    // Free-form manual pairing: link a neighbour hold to a new-panel hold the matcher never proposed.
    // Independent of the proposal steps (which live in the fixed-length _decisions array), so manual
    // links get their own list and go in alongside the decisions at Finish. Works with zero proposals.
    private bool _manualMode;
    private Guid? _manualNeighborId;
    private Guid? _manualLeftId;
    private Guid? _manualRightId;
    private int _manualFocusKey;
    private readonly List<ConfirmedLink> _manualLinks = [];
    private List<WallPanelInfo> _neighborPanels = [];

    private ElementReference _rootRef;
    private Dictionary<Guid, PanelHold> _stagedHolds = [];
    private List<PanelHold> _stagedList = [];
    private readonly Dictionary<Guid, IReadOnlyList<PanelHold>> _neighborHolds = [];

    protected override async Task OnInitializedAsync()
    {
        _steps = Proposals.OrderByDescending(p => p.Confidence).ToList();
        _decisions = new ConfirmedLink?[_steps.Count];

        var staged = await WallPanelService.GetPanelHoldsAsync(WallId, PanelId, includeStaged: true);
        _stagedHolds = staged.ToDictionary(h => h.Id);
        _stagedList = staged.ToList();

        // The adjacent live neighbours are the left-hand candidates for manual linking — including
        // ones the matcher produced no proposal for, so manual mode works even with zero proposals.
        // Computed from this new panel's grid position against the wall's live panels.
        var neighborPositions = new HashSet<(int Col, int Row)>
        {
            (Col - 1, Row), (Col + 1, Row), (Col, Row - 1), (Col, Row + 1),
        };
        var panels = await WallPanelService.GetPanelsAsync(WallId);
        _neighborPanels = panels
            .Where(p => p.IsLive && neighborPositions.Contains((p.Col, p.Row)))
            .ToList();
        _manualNeighborId = _neighborPanels.FirstOrDefault()?.Id;

        // Load holds for every neighbour we might show: the proposal steps' neighbours (proposal
        // stepping) plus the grid-adjacent live neighbours (manual linking). Union so neither path
        // is starved even if the two sets ever diverge.
        var neighborIds = _neighborPanels
            .Select(p => p.Id)
            .Union(_steps.Select(p => p.NeighborPanelId))
            .Distinct();
        foreach (var neighborId in neighborIds)
        {
            _neighborHolds[neighborId] = await WallPanelService.GetPanelHoldsAsync(WallId, neighborId, includeStaged: false);
        }

        _loading = false;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Attach the JS scroll-suppression trap to the (always-rendered) root once it exists.
        await AttachKeyTrapAsync(firstRender);

        // Return focus to the root after every state transition so a previously mouse-clicked
        // button doesn't retain focus and double-fire an Enter with the root's keydown handler.
        // preventScroll keeps the re-centred images from being scrolled by the focus call.
        if (_refocus && !_finishing)
        {
            _refocus = false;
            try
            {
                await _rootRef.FocusAsync(preventScroll: true);
            }
            catch (Exception)
            {
                // Focus is a nicety for keyboard shortcuts; never fatal.
            }
        }
    }

    // ---- Decisions -------------------------------------------------------------
    private void ConfirmMatch()
    {
        var step = _steps[_index];
        if (!TryRecord(new ConfirmedLink(step.HoldAId, step.HoldBId, Moved: false)))
        {
            return;
        }

        Next();
    }

    private void DiscardMatch()
    {
        _decisions[_index] = null;
        Next();
    }

    /// <summary>
    /// The neighbour hold under review has been physically removed from the wall. Records its id
    /// for deletion at the final Confirm and drops any link for it (removal wins), then advances.
    /// </summary>
    private void DeleteHold()
    {
        var neighborHoldId = _steps[_index].HoldAId;
        _removed.Add(neighborHoldId);

        // A removed hold cannot be a link endpoint: clear any decision (this step or an earlier
        // one) that used it so the accelerator/finish never tries to link a hold that is gone.
        for (var i = 0; i < _decisions.Length; i++)
        {
            if (_decisions[i] is { } d && d.NeighborHoldId == neighborHoldId)
            {
                _decisions[i] = null;
            }
        }

        Next();
    }

    private void EnterMoved()
    {
        _movedMode = true;
        _addMode = false;
        _movedSelectedHoldId = null;
        _warning = null;
        _refocus = true;
    }

    private void CancelMoved()
    {
        _movedMode = false;
        _addMode = false;
        _movedSelectedHoldId = null;
        _refocus = true;
    }

    private void OnMovedHoldTap(Guid holdId) => _movedSelectedHoldId = holdId;

    private async Task OnMovedAddTap((double X, double Y) at)
    {
        var id = await WallPanelService.AddPanelHoldAsync(WallId, PanelId, at.X, at.Y, 0.02);
        var hold = new PanelHold(id, at.X, at.Y, 0.02, null);
        _stagedHolds[id] = hold;
        _stagedList = _stagedList.Append(hold).ToList();
        _movedSelectedHoldId = id;
        _addMode = false;
    }

    private void UseMovedHold()
    {
        var step = _steps[_index];
        if (_movedSelectedHoldId is not { } chosen)
        {
            return;
        }

        if (!TryRecord(new ConfirmedLink(step.HoldAId, chosen, Moved: true)))
        {
            return;
        }

        CancelMoved();
        Next();
    }

    // ---- Manual linking --------------------------------------------------------
    private void EnterManual()
    {
        if (_neighborPanels.Count == 0)
        {
            return;
        }

        _manualMode = true;
        _movedMode = false;
        _addMode = false;
        _manualLeftId = null;
        _manualRightId = null;
        _manualNeighborId ??= _neighborPanels.First().Id;
        _warning = null;
        _manualFocusKey++;
        _refocus = true;
    }

    private void CancelManual()
    {
        _manualMode = false;
        _manualLeftId = null;
        _manualRightId = null;
        _warning = null;
        _refocus = true;
    }

    private void OnManualLeftTap(Guid holdId) => _manualLeftId = holdId;

    private void OnManualRightTap(Guid holdId) => _manualRightId = holdId;

    private void SelectManualNeighbor(Guid neighborId)
    {
        _manualNeighborId = neighborId;
        // The left selection belongs to a specific neighbour; drop it when switching neighbours.
        _manualLeftId = null;
        _manualFocusKey++;
    }

    /// <summary>
    /// Records a free-form neighbour-hold ↔ new-hold pair the matcher never proposed, then clears the
    /// two selections so the user can pair more. Reuses the same "one new hold, one link" guard as the
    /// proposal steps and dedupes against links already marked manually.
    /// </summary>
    private void MarkManualOverlap()
    {
        if (_manualLeftId is not { } left || _manualRightId is not { } right)
        {
            return;
        }

        if (IsNewHoldTaken(right, exceptIndex: -1) || _manualLinks.Any(l => l.NewHoldId == right))
        {
            _warning = "That hold is already linked to another neighbour hold — pick a different one.";
            return;
        }

        if (_manualLinks.Any(l => l.NeighborHoldId == left && l.NewHoldId == right))
        {
            _warning = "Those two holds are already marked as overlapping.";
            return;
        }

        _manualLinks.Add(new ConfirmedLink(left, right, Moved: false));
        _manualLeftId = null;
        _manualRightId = null;
        _warning = null;
    }

    /// <summary>Records a decision, guarding against linking the same new hold from two steps.</summary>
    private bool TryRecord(ConfirmedLink link)
    {
        for (var i = 0; i < _decisions.Length; i++)
        {
            if (i != _index && _decisions[i] is { } other && other.NewHoldId == link.NewHoldId)
            {
                _warning = "That hold is already linked to another neighbour hold — pick a different one.";
                return false;
            }
        }

        _decisions[_index] = link;
        _warning = null;
        return true;
    }

    private void Next()
    {
        _warning = null;
        _refocus = true;
        if (_index < _steps.Count - 1)
        {
            _index++;
        }
        else
        {
            _ = Finish();
        }
    }

    private void Back()
    {
        _warning = null;
        _refocus = true;
        if (_index > 0)
        {
            _index--;
        }
    }

    private async Task ConfirmAllHighConfidence()
    {
        for (var i = _index; i < _steps.Count; i++)
        {
            var p = _steps[i];
            if (p.Confidence < HighConfidence || _decisions[i] is not null || _removed.Contains(p.HoldAId))
            {
                continue;
            }

            if (!IsNewHoldTaken(p.HoldBId, i))
            {
                _decisions[i] = new ConfirmedLink(p.HoldAId, p.HoldBId, Moved: false);
            }
        }

        await Finish();
    }

    private bool IsNewHoldTaken(Guid newHoldId, int exceptIndex)
    {
        for (var i = 0; i < _decisions.Length; i++)
        {
            if (i != exceptIndex && _decisions[i] is { } d && d.NewHoldId == newHoldId)
            {
                return true;
            }
        }

        return false;
    }

    private async Task Finish()
    {
        _finishing = true;
        var links = _decisions
            .Where(d => d is not null)
            .Select(d => d!)
            .Concat(_manualLinks)
            .Where(d => !_removed.Contains(d.NeighborHoldId))
            .ToList();
        await OnConfirm.InvokeAsync(new PanelConfirmation(links, _removed.ToList()));
    }

    private Task Discard() => OnDiscard.InvokeAsync();
}
