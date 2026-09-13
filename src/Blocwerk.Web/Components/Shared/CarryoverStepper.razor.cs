using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// A focused, one-at-a-time review of the carryover decisions that actually matter, reusing
/// <see cref="PanelImageView"/> for the old (left) and new-centre (right) images. State and
/// navigation live here; the markup is in the .razor.
/// Every old hold is CARRIED unchanged by default. The primary control is a per-hold
/// "has physically changed" toggle (off => <see cref="CarryKind.Carried"/>, on =>
/// <see cref="CarryKind.Changed"/>); manual re-target (click old -> click new) is always available
/// and only sets the new-hold association; Remove is the distinct "physically gone" action.
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

    // Per-hold state, keyed by the OLD hold id so it survives back/forward navigation.
    // "changed" is 100% manual — never seeded from the matcher.
    private readonly HashSet<Guid> _changed = [];

    // Manual re-target result: old hold id -> chosen new hold id. Overrides the matcher's suggestion
    // carried on the item. Independent of the "changed" toggle.
    private readonly Dictionary<Guid, Guid> _retargeted = [];

    private string OldPhotoUrl => $"/api/walls/{WallId}/photo";
    private string NewPhotoUrl => $"/api/walls/{WallId}/panels/{CenterPanelId}/staged-photo";

    protected override void OnParametersSet()
    {
        _oldById = OldHolds.ToDictionary(h => h.Id);
        _newById = NewHolds.ToDictionary(h => h.Id);

        // Seed the per-hold state from the PERSISTED decisions carried on each item. The parent's
        // _decisions are the single source of truth, so a reopened stepper reflects earlier choices
        // instead of resetting every hold to a blank "carried" — otherwise Accept-walking a second
        // time would re-emit Carried and silently drop previously-set "physically changed" flags.
        _changed.Clear();
        _retargeted.Clear();
        foreach (var item in Items)
        {
            if (item.OldHoldId is not { } old)
            {
                continue;
            }

            if (item.Kind == CarryKind.Changed)
            {
                _changed.Add(old);
            }

            if (item.NewHoldId is { } nid)
            {
                _retargeted[old] = nid;
            }
        }
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

    // The new hold this old hold maps to: a manual re-target wins over the item's matcher suggestion.
    private Guid? EffectiveNewHoldId
    {
        get
        {
            if (Current?.OldHoldId is { } old && _retargeted.TryGetValue(old, out var chosen))
            {
                return chosen;
            }

            return Current?.NewHoldId;
        }
    }

    private PanelHold? NewHold => EffectiveNewHoldId is { } id ? _newById.GetValueOrDefault(id) : null;

    private bool IsChanged => Current?.OldHoldId is { } old && _changed.Contains(old);

    private CarryKind CurrentKind => IsChanged ? CarryKind.Changed : CarryKind.Carried;

    // An old hold that maps to a new hold (matcher suggestion or manual re-target) is a linked pair —
    // badge both green so they read as one link across the two images (the R&D green-dot cue).
    // Otherwise the old hold is amber and the new-centre image is neutral blue.
    private bool HasLink => Mode != CarryReviewMode.New && EffectiveNewHoldId is not null;

    private static (double X, double Y)? Point(PanelHold? h) => h is null ? null : (h.X, h.Y);

    private const string LinkGreen = "#33dd66";
    private string LeftBadgeColor => HasLink ? LinkGreen : "#ffb020";
    private string RightBadgeColor => HasLink ? LinkGreen : "#4aa8ff";

    private string RightCaption =>
        _interactive ? "New centre — tap the matching hold"
        : HasLink ? "New centre — mapped hold"
        : "New centre (after)";

    private string ModeTitle => Mode switch
    {
        CarryReviewMode.Uncertain => "Confirm the holds that changed or moved",
        CarryReviewMode.Carried => "Review carried-over holds",
        CarryReviewMode.Removal => "Review holds we could not re-find",
        CarryReviewMode.New => "Spot-check new holds",
        _ => "Review",
    };

    private string ModeHint => Mode switch
    {
        CarryReviewMode.Uncertain => "These are the only holds the matcher was unsure about, most-moved first. Accept to keep it as carried, flag it as physically changed, re-target it, or remove it if it is gone. Walk past the rest — everything else already carried cleanly.",
        CarryReviewMode.Carried => "Each hold is carried over unchanged by default. Flag it as physically changed, re-target it to the right hold, or remove it if it is gone.",
        CarryReviewMode.Removal => "We could not confidently map this old hold. It is CARRIED by default — re-target it, flag it as changed, or only remove it if it is really gone.",
        CarryReviewMode.New => "This hold was detected only on the new photo. Keep it, or discard an obvious false detection.",
        _ => string.Empty,
    };

    // ---- Actions ---------------------------------------------------------------

    // Records the current per-hold decision: Carried by default, Changed when the toggle is on,
    // always with the effective (possibly re-targeted) new-hold association.
    private async Task EmitCarry()
    {
        if (Current?.OldHoldId is { } old)
        {
            await OnCarryDecision.InvokeAsync(new CarryDecisionChange(old, CurrentKind, EffectiveNewHoldId));
        }
    }

    private async Task AcceptCarry()
    {
        await EmitCarry();
        Next();
    }

    private async Task ToggleChanged(ChangeEventArgs e)
    {
        if (Current?.OldHoldId is not { } old)
        {
            return;
        }

        if (e.Value is true)
        {
            _changed.Add(old);
        }
        else
        {
            _changed.Remove(old);
        }

        await EmitCarry();
    }

    private async Task ToggleChangedKey()
    {
        if (Current?.OldHoldId is not { } old)
        {
            return;
        }

        if (!_changed.Add(old))
        {
            _changed.Remove(old);
        }

        await EmitCarry();
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
        _selectedNewId = EffectiveNewHoldId;
        _refocus = true;
    }

    private void CancelInteractive()
    {
        _interactive = false;
        _selectedNewId = null;
        _refocus = true;
    }

    private void OnNewHoldTap(Guid holdId) => _selectedNewId = holdId;

    // Manual re-target only records "this old hold is this new hold". It is independent of the
    // "changed" toggle, and it does NOT advance — the user may still toggle changed or remove.
    private async Task UseInteractive()
    {
        if (Current?.OldHoldId is { } old && _selectedNewId is { } chosen)
        {
            _retargeted[old] = chosen;
            _interactive = false;
            _selectedNewId = null;
            _refocus = true;
            await EmitCarry();
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
        _interactive = false;
        _selectedNewId = null;
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
                await Primary();
                break;
            case "c":
            case "C":
                if (Mode != CarryReviewMode.New)
                {
                    await ToggleChangedKey();
                }

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

    private Task Primary() => Mode == CarryReviewMode.New ? KeepNew() : AcceptCarry();
}
