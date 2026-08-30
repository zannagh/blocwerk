using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// A focused, one-at-a-time review of the carryover decisions that actually matter (moved holds,
/// removal candidates, or new detections), reusing <see cref="PanelImageView"/> for the old (left)
/// and new-centre (right) images. State and navigation live here; the markup is in the .razor.
/// Every action defaults towards KEEP — the user must explicitly choose to remove or discard.
/// </summary>
public partial class CarryoverStepper
{
    [Parameter] public Guid WallId { get; set; }
    [Parameter] public Guid CenterPanelId { get; set; }
    [Parameter] public CarryReviewMode Mode { get; set; }
    [Parameter] public IReadOnlyList<CarryReviewItem> Items { get; set; } = [];
    [Parameter] public IReadOnlyList<PanelHold> OldHolds { get; set; } = [];
    [Parameter] public IReadOnlyList<PanelHold> NewHolds { get; set; } = [];

    [Parameter] public EventCallback<CarryDecisionChange> OnCarryDecision { get; set; }
    [Parameter] public EventCallback<NewDecisionChange> OnNewDecision { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }

    private int _index;
    private bool _interactive;
    private Guid? _selectedNewId;

    private ElementReference _rootRef;
    private bool _refocus = true;

    private Dictionary<Guid, PanelHold> _oldById = [];
    private Dictionary<Guid, PanelHold> _newById = [];

    private string OldPhotoUrl => $"/api/walls/{WallId}/photo";
    private string NewPhotoUrl => $"/api/walls/{WallId}/panels/{CenterPanelId}/staged-photo";

    protected override void OnParametersSet()
    {
        _oldById = OldHolds.ToDictionary(h => h.Id);
        _newById = NewHolds.ToDictionary(h => h.Id);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_refocus)
        {
            _refocus = false;
            try
            {
                await _rootRef.FocusAsync(preventScroll: true);
            }
            catch (Exception)
            {
                // Focus is a keyboard-shortcut nicety; never fatal.
            }
        }
    }

    private CarryReviewItem? Current => _index >= 0 && _index < Items.Count ? Items[_index] : null;

    private PanelHold? OldHold => Current?.OldHoldId is { } id ? _oldById.GetValueOrDefault(id) : null;
    private PanelHold? NewHold => Current?.NewHoldId is { } id ? _newById.GetValueOrDefault(id) : null;

    private static (double X, double Y)? Point(PanelHold? h) => h is null ? null : (h.X, h.Y);

    // A moved hold and its detected new position form a linked pair — badge both green so they read
    // as one link across the two images (the R&D green-dot cue). Other modes keep old=amber/new=blue.
    private const string LinkGreen = "#33dd66";
    private string LeftBadgeColor => Mode == CarryReviewMode.Moved ? LinkGreen : "#ffb020";
    private string RightBadgeColor => Mode == CarryReviewMode.Moved ? LinkGreen : "#4aa8ff";

    private string RightCaption =>
        _interactive ? "New centre — tap the matching hold"
        : Mode == CarryReviewMode.Moved && Current?.NewHoldId is not null ? "New centre — detected new position"
        : "New centre (after)";

    private string ModeTitle => Mode switch
    {
        CarryReviewMode.Moved => "Review moved holds",
        CarryReviewMode.Removal => "Review holds we could not re-find",
        CarryReviewMode.New => "Spot-check new holds",
        _ => "Review",
    };

    private string ModeHint => Mode switch
    {
        CarryReviewMode.Moved => "The matcher thinks this hold moved. Confirm it, re-target it, or remove it.",
        CarryReviewMode.Removal => "We could not find this old hold on the new photo. It is KEPT by default — only remove it if it is really gone.",
        CarryReviewMode.New => "This hold was detected only on the new photo. Keep it, or discard an obvious false detection.",
        _ => string.Empty,
    };

    // ---- Actions ---------------------------------------------------------------
    private async Task KeepMoved()
    {
        if (Current?.OldHoldId is { } old)
        {
            await OnCarryDecision.InvokeAsync(new CarryDecisionChange(old, CarryKind.Moved, Current.NewHoldId));
        }

        Next();
    }

    private async Task KeepInPlace()
    {
        if (Current?.OldHoldId is { } old)
        {
            // Not moved: keep the identity, seated on the matched position when there is one.
            var kind = CarryKind.Carried;
            await OnCarryDecision.InvokeAsync(new CarryDecisionChange(old, kind, Current.NewHoldId));
        }

        Next();
    }

    private async Task RemoveOld()
    {
        if (Current?.OldHoldId is { } old)
        {
            await OnCarryDecision.InvokeAsync(new CarryDecisionChange(old, CarryKind.Removed, null));
        }

        Next();
    }

    private async Task KeepNew()
    {
        if (Current?.NewHoldId is { } id)
        {
            await OnNewDecision.InvokeAsync(new NewDecisionChange(id, Discarded: false));
        }

        Next();
    }

    private async Task DiscardNew()
    {
        if (Current?.NewHoldId is { } id)
        {
            await OnNewDecision.InvokeAsync(new NewDecisionChange(id, Discarded: true));
        }

        Next();
    }

    private void EnterInteractive()
    {
        _interactive = true;
        _selectedNewId = Current?.NewHoldId;
        _refocus = true;
    }

    private void CancelInteractive()
    {
        _interactive = false;
        _selectedNewId = null;
        _refocus = true;
    }

    private void OnNewHoldTap(Guid holdId) => _selectedNewId = holdId;

    private async Task UseInteractive()
    {
        if (Current?.OldHoldId is { } old && _selectedNewId is { } chosen)
        {
            // Re-targeting/re-finding gives the old hold a new position, so record it as Moved.
            await OnCarryDecision.InvokeAsync(new CarryDecisionChange(old, CarryKind.Moved, chosen));
            CancelInteractive();
            Next();
        }
    }

    private void Next()
    {
        _interactive = false;
        _selectedNewId = null;
        _refocus = true;
        if (_index < Items.Count - 1)
        {
            _index++;
        }
        else
        {
            _ = OnClose.InvokeAsync();
        }
    }

    private void Back()
    {
        _refocus = true;
        if (_index > 0)
        {
            _index--;
        }
    }

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (Items.Count == 0)
        {
            return;
        }

        if (_interactive)
        {
            switch (e.Key)
            {
                case "Enter":
                    await UseInteractive();
                    break;
                case "Escape":
                    CancelInteractive();
                    break;
            }

            return;
        }

        switch (e.Key)
        {
            case "Enter":
                await PrimaryKeep();
                break;
            case "x":
            case "X":
            case "Delete":
                if (Mode == CarryReviewMode.New)
                {
                    await DiscardNew();
                }
                else
                {
                    await RemoveOld();
                }

                break;
            case "ArrowRight":
                Next();
                break;
            case "ArrowLeft":
                Back();
                break;
        }
    }

    private Task PrimaryKeep() => Mode switch
    {
        CarryReviewMode.Moved => KeepMoved(),
        CarryReviewMode.Removal => KeepInPlace(),
        CarryReviewMode.New => KeepNew(),
        _ => Task.CompletedTask,
    };
}
