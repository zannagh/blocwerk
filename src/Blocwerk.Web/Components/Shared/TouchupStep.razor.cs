using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The manual hold touch-up surface of the big-wall update. It is used twice: once right after
/// detection and BEFORE any matching runs (so corrections feed the carryover/overlap proposals), and
/// once as the final pass after the neighbour overlaps, before the confirm/apply step. The behaviour
/// is identical in both — only the copy differs, via <see cref="Heading"/> and the lead parameters.
/// Lets the user walk EVERY staged panel of the new generation and correct
/// the model's detection: add a missed hold, drag/resize a hold into place, or delete a stray one.
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
    private bool _addMode;
    private Guid? _selectedHoldId;
    private List<PanelHold> _holds = [];
    private List<StagedPanelRef> _panels = [];

    private StagedPanelRef? CurrentPanel =>
        _panelIndex >= 0 && _panelIndex < _panels.Count ? _panels[_panelIndex] : null;

    /// <summary>
    /// Keyboard entry point for the wizard's "a" binding (toggle add-hold mode). The wizard owns the
    /// single shortcut scope, so the key arrives here from outside a Blazor event and this step has
    /// to ask for its own re-render.
    /// </summary>
    public void TryToggleAddMode()
    {
        if (_loading || CurrentPanel is null)
        {
            return;
        }

        ToggleAddMode();
        StateHasChanged();
    }

    /// <summary>
    /// Keyboard entry point for the wizard's "x" binding (remove the selected hold). Mirrors the
    /// Remove button's disabled state: with nothing selected the key does nothing at all.
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
        _addMode = false;
        _selectedHoldId = null;
        await ReloadHoldsAsync();
    }

    private void SelectHold(Guid id) => _selectedHoldId = id;

    private void ToggleAddMode()
    {
        _addMode = !_addMode;
        if (_addMode)
        {
            _selectedHoldId = null;
        }
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
            WallId, panel.PanelId, at.X, at.Y, DefaultNewHoldRadius, needsReview: false);
        _addMode = false;
        _selectedHoldId = id;
        await ReloadHoldsAsync();
    }

    private async Task RemoveSelectedHold()
    {
        if (_selectedHoldId is not { } id)
        {
            return;
        }

        await WallPanelService.DeleteStagedHoldAsync(WallId, id);
        _selectedHoldId = null;
        await ReloadHoldsAsync();
    }

    /// <summary>A staged panel the touch-up step can edit: its id, a human label, and grid position.</summary>
    private sealed record StagedPanelRef(Guid PanelId, string Label, int Col, int Row);
}
