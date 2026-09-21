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

    /// <summary>
    /// Whether the "existing neighbour" (left) panel is itself STAGED rather than live-committed.
    /// The add-panel flow links a new panel to an already-live neighbour (committed <c>/photo</c>,
    /// committed holds) — the default, false. The big-wall update links every staged panel to the
    /// staged CENTRE panel, which has no committed photo of its own yet (its bytes live on
    /// <c>/staged-photo</c> at generation N+1); passing true makes the left side read the staged photo
    /// and the staged holds so it resolves instead of 404ing on a nonexistent committed blob.
    /// </summary>
    [Parameter] public bool NeighbourStaged { get; set; }
    [Parameter] public EventCallback<PanelConfirmation> OnConfirm { get; set; }
    [Parameter] public EventCallback OnDiscard { get; set; }

    private const double HighConfidence = 0.90;

    // The neighbour-panel holds that carry a live boulder, for the boulder-first step order.
    private HashSet<Guid> _boulderHoldIds = [];

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
        // Proposals about a hold a LIVE boulder is built from come first: those are the links whose
        // loss actually breaks something. One query per wall (already twin-expanded), read once here;
        // a failure just leaves the previous confidence-only order.
        try
        {
            var usage = await BoulderService.GetHoldUsageAsync(WallId);
            _boulderHoldIds = HoldReviewOrdering.LiveBoulderHoldIds(usage);
        }
        catch (Exception)
        {
            _boulderHoldIds = [];
        }

        _steps = HoldReviewOrdering.BoulderFirstThenByDescending(
            Proposals,
            p => p.HoldAId,
            p => p.Confidence,
            _boulderHoldIds);
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
            _neighborHolds[neighborId] = await WallPanelService.GetPanelHoldsAsync(WallId, neighborId, includeStaged: NeighbourStaged);
        }

        // Lay any already-confirmed outcome for this panel back over the fresh walk. See the Resume partial.
        SeedFromRestored();

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
    private async Task ConfirmMatch()
    {
        var step = _steps[_index];
        if (!TryRecord(new ConfirmedLink(step.HoldAId, step.HoldBId, Moved: false)))
        {
            return;
        }

        await Next();
    }

    private async Task DiscardMatch()
    {
        _decisions[_index] = null;

        // "Not the same hold" is a decision, so this step is no longer one the bin tool owes the user.
        ClearUndecided(_index);
        await Next();
    }

    /// <summary>
    /// The neighbour hold under review has been physically removed from the wall. Records its id
    /// for deletion at the final Confirm and drops any link for it (removal wins), then advances.
    /// </summary>
    private async Task DeleteHold()
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

            // A hold the user says is gone is settled either way: never re-offer its steps, whether the
            // bin tool un-decided them or this removal just did.
            if (_steps[i].HoldAId == neighborHoldId)
            {
                ClearUndecided(i);
            }
        }

        await Next();
    }

    /// <summary>Records a decision, guarding against linking the same new hold from two steps.</summary>
    private bool TryRecord(ConfirmedLink link)
    {
        if (IsNewHoldTaken(link.NewHoldId, _index))
        {
            _warning = "That hold is already linked to another neighbour hold — pick a different one.";
            return false;
        }

        _decisions[_index] = link;
        ClearUndecided(_index);
        _warning = null;
        return true;
    }

    // Every path here has just recorded (or deliberately declined) a decision, so this is the natural
    // save point for the panel: one round-trip per step, never one per render.
    private async Task Next()
    {
        _warning = null;
        _refocus = true;
        if (_index < _steps.Count - 1)
        {
            _index++;
            await ReportProgressAsync();
        }
        else
        {
            await Finish();
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
                ClearUndecided(i);
            }
        }

        await Finish();
    }

    /// <summary>
    /// Whether some other link already claims this new-panel hold. Spans BOTH halves of the outcome —
    /// the proposal decisions and the standalone <c>_manualLinks</c> — because both end up in the
    /// confirmation, and two links onto one new hold become two HoldLink rows for the same HoldB at
    /// promote. A resumed walk seeds restored links into <c>_manualLinks</c>, so scanning only the
    /// decisions let the user confirm a proposal onto a hold a restored link had already taken.
    /// </summary>
    private bool IsNewHoldTaken(Guid newHoldId, int exceptIndex)
    {
        for (var i = 0; i < _decisions.Length; i++)
        {
            if (i != exceptIndex && _decisions[i] is { } d && d.NewHoldId == newHoldId)
            {
                return true;
            }
        }

        return _manualLinks.Any(l => l.NewHoldId == newHoldId);
    }

    private async Task Finish()
    {
        // A link the bin tool took away is the user's own work: never close the panel over one without
        // showing it to them first. See TryDeflectToUndecided — it gives way after saying so once.
        if (TryDeflectToUndecided())
        {
            return;
        }

        _finishing = true;
        await OnConfirm.InvokeAsync(CurrentOutcome());
    }

    private Task Discard() => OnDiscard.InvokeAsync();
}
