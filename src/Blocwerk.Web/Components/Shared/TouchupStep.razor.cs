using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The manual hold touch-up surface of the big-wall update. It is used twice: once right after
/// detection and BEFORE any matching runs (so corrections feed the carryover/overlap proposals), and
/// once as the final pass after the neighbour overlaps, before the confirm/apply step. The behaviour
/// is identical in both — only the copy differs, via <see cref="Heading"/> and the lead parameters.
/// Lets the user walk EVERY staged panel of the new generation and correct
/// the model's detection: add a missed hold, drag one into place, resize it from the toolbar, or
/// delete a stray one.
/// These are corrections, not physical changes — added holds are staged with <c>needsReview:false</c>
/// and repositions only touch geometry, so nothing here flags a hold or boulder for review. All
/// edits go straight to the staged rows via <see cref="IWallPanelService"/>; the physical-change flag
/// is emitted only by the carryover stepper, never from here. State lives here; markup in the .razor.
/// </summary>
public partial class TouchupStep
{
    [Parameter] public Guid WallId { get; set; }

    /// <summary>The in-flight update session: its centre + neighbour panel ids are the staged panels.</summary>
    [Parameter] public BigUpdateSession Session { get; set; } = default!;

    /// <summary>Raised when the user is happy with the touch-up and continues to the confirm step.</summary>
    [Parameter] public EventCallback OnContinue { get; set; }

    /// <summary>Raised when the user skips the touch-up entirely; the parent also lands on confirm.</summary>
    [Parameter] public EventCallback OnSkip { get; set; }

    /// <summary>The step label above the editor; defaults to the final-pass wording.</summary>
    [Parameter] public string Heading { get; set; } = "Touch up the detected holds";

    /// <summary>The bold lead line of the explanatory box; defaults to the final-pass wording.</summary>
    [Parameter] public string Lead { get; set; } = "Fix anything the detector got wrong.";

    /// <summary>
    /// The sub-line of the explanatory box. Null renders the default "these are corrections, nothing
    /// gets flagged for review" note that applies to both passes.
    /// </summary>
    [Parameter] public RenderFragment? LeadDetail { get; set; }

    [Inject]
    private IWallPanelService WallPanelService { get; set; } = default!;

    // Normalized default radius for a user-added hold (~2% of the panel), matching the review pane.
    private const double DefaultNewHoldRadius = 0.02;

    private bool _loading = true;
    private int _panelIndex;

    // The active toolbar tool, and the size newly added holds get. The size starts at the default and
    // is then whatever the pipette sampled or the slider last set, so a run of adds no longer has to be
    // resized one hold at a time.
    private HoldTouchupTool _tool;
    private double _newHoldRadius = DefaultNewHoldRadius;
    private Guid? _selectedHoldId;
    private List<PanelHold> _holds = [];
    private List<StagedPanelRef> _panels = [];

    private StagedPanelRef? CurrentPanel =>
        _panelIndex >= 0 && _panelIndex < _panels.Count ? _panels[_panelIndex] : null;

    /// <summary>
    /// Keyboard entry point for the wizard's tool bindings (a / d / p). Picking the tool that is
    /// already active drops back out of it, exactly like clicking its toolbar button. The wizard owns
    /// the single shortcut scope, so the key arrives here from outside a Blazor event and this step
    /// has to ask for its own re-render.
    /// </summary>
    public void TrySelectTool(HoldTouchupTool tool)
    {
        if (_loading || CurrentPanel is null)
        {
            return;
        }

        SetTool(_tool == tool ? HoldTouchupTool.None : tool);
        StateHasChanged();
    }

    /// <summary>
    /// Keyboard entry point for the wizard's "x" binding (remove the selected hold). With nothing
    /// selected the key does nothing at all — the bin TOOL deletes by tapping a hold instead.
    /// </summary>
    public async Task TryRemoveSelectedHoldAsync()
    {
        if (_loading || _selectedHoldId is null)
        {
            return;
        }

        await RemoveSelectedHold();
        StateHasChanged();
    }

    protected override async Task OnInitializedAsync()
    {
        // The staged panels of this update are exactly the centre plus every neighbour the session
        // enumerated; GetPanelsAsync can't be used because it dedupes cells to the LIVE panel and so
        // hides the staged rows mid-flow.
        _panels =
        [
            new StagedPanelRef(Session.CenterPanelId, "Centre", 0, 0),
            .. Session.Neighbours.Select(n => new StagedPanelRef(n.PanelId, $"Panel ({n.Col}, {n.Row})", n.Col, n.Row)),
        ];

        await ReloadHoldsAsync();
        _loading = false;
    }

    private string StagedPhotoUrl(Guid panelId) => $"/api/walls/{WallId}/panels/{panelId}/staged-photo";

    private async Task ReloadHoldsAsync()
    {
        if (CurrentPanel is not { } panel)
        {
            _holds = [];
            return;
        }

        _holds = (await WallPanelService.GetPanelHoldsAsync(WallId, panel.PanelId, includeStaged: true)).ToList();
    }

    private async Task SelectPanel(int index)
    {
        if (index < 0 || index >= _panels.Count || index == _panelIndex)
        {
            return;
        }

        _panelIndex = index;
        _tool = HoldTouchupTool.None;
        _selectedHoldId = null;
        await ReloadHoldsAsync();
    }

    /// <summary>
    /// A tap on a hold, routed by the active tool: the bin deletes it outright (editor semantics), the
    /// pipette adopts its size for the next adds, and otherwise it is simply selected.
    /// </summary>
    private async Task OnHoldTapAsync(Guid id)
    {
        switch (_tool)
        {
            case HoldTouchupTool.Delete:
                await RemoveHoldAsync(id);
                break;

            case HoldTouchupTool.Pipette:
                SampleSize(id);
                break;

            default:
                SelectHold(id);
                break;
        }
    }

    /// <summary>
    /// Selects a hold AND adopts its radius as the toolbar's size, exactly as the wall editor does on
    /// select and on drag-start. Without this the slider still showed the last add-size while its label
    /// read "Size (selected):", so one nudge resized the selected hold to a value the user never chose —
    /// a 40% shrink on a hold larger than the default.
    /// </summary>
    private void SelectHold(Guid id)
    {
        _selectedHoldId = id;
        if (_holds.FirstOrDefault(h => h.Id == id) is { } hold)
        {
            _newHoldRadius = hold.Radius;
        }
    }

    // Pipette: take the tapped hold's radius as the current size and drop straight into Add mode, which
    // is what the sample is for. The selection is cleared so the slider unambiguously describes the size
    // of the NEXT hold rather than silently resizing the hold that was just sampled.
    private void SampleSize(Guid id)
    {
        if (_holds.FirstOrDefault(h => h.Id == id) is { } hold)
        {
            _newHoldRadius = hold.Radius;
        }

        _selectedHoldId = null;
        _tool = HoldTouchupTool.Add;
    }

    // Mutually exclusive tools, mirroring the wall editor's SetMode: leaving a tool clears the transient
    // state that only made sense inside it.
    private void SetTool(HoldTouchupTool tool)
    {
        _tool = tool;
        if (tool is HoldTouchupTool.Add or HoldTouchupTool.Pipette)
        {
            _selectedHoldId = null;
        }
    }

    // Live slider feedback: the size a new hold gets always follows the slider.
    private void OnSizeChanged(double radius) => _newHoldRadius = radius;

    // Slider released: with a hold selected that hold is resized, through the same geometry/persistence
    // path a drag uses. With nothing selected the slider only set the size for the next add.
    private async Task OnSizeCommittedAsync(double radius)
    {
        if (_selectedHoldId is not { } id || _holds.FirstOrDefault(h => h.Id == id) is not { } hold)
        {
            return;
        }

        await OnHoldGeometryChanged(new PanelImageView.HoldGeometry(id, hold.X, hold.Y, radius));
    }

    private async Task OnHoldGeometryChanged(PanelImageView.HoldGeometry g)
    {
        // Geometry-only: never sets any review/changed flag, so a reposition is a pure correction.
        await WallPanelService.UpdateStagedHoldAsync(WallId, g.HoldId, g.X, g.Y, g.Radius);
        await ReloadHoldsAsync();
    }

    private async Task OnEmptyTap((double X, double Y) at)
    {
        if (CurrentPanel is not { } panel)
        {
            return;
        }

        // needsReview:false — a hold the user adds here is a correction of a model miss, not a
        // physical change, so it must not be flagged for review.
        var id = await WallPanelService.AddStagedHoldAsync(
            WallId, panel.PanelId, at.X, at.Y, _newHoldRadius, needsReview: false);

        // The Add tool stays active so a run of misses can be fixed in one go; the fresh hold is
        // selected so the slider can fine-tune it straight away.
        _selectedHoldId = id;
        await ReloadHoldsAsync();
    }

    private async Task RemoveSelectedHold()
    {
        if (_selectedHoldId is not { } id)
        {
            return;
        }

        await RemoveHoldAsync(id);
    }

    private async Task RemoveHoldAsync(Guid id)
    {
        await WallPanelService.DeleteStagedHoldAsync(WallId, id);
        if (_selectedHoldId == id)
        {
            _selectedHoldId = null;
        }

        await ReloadHoldsAsync();
    }

    /// <summary>A staged panel the touch-up step can edit: its id, a human label, and grid position.</summary>
    private sealed record StagedPanelRef(Guid PanelId, string Label, int Col, int Row);
}
