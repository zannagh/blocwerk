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

        foreach (var neighborId in _steps.Select(p => p.NeighborPanelId).Distinct())
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
            .Where(d => !_removed.Contains(d.NeighborHoldId))
            .ToList();
        await OnConfirm.InvokeAsync(new PanelConfirmation(links, _removed.ToList()));
    }

    private Task Discard() => OnDiscard.InvokeAsync();
}
