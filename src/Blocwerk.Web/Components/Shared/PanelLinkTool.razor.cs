using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Standalone tool to declare (or break) that a hold on one big-wall panel and a hold on another are
/// the same physical hold across an overlapping seam. State and the create/break calls live here; the
/// two <see cref="PanelImageView"/>s, panel picker and existing-links list are in PanelLinkTool.razor.
/// </summary>
public partial class PanelLinkTool
{
    [Parameter] public Guid WallId { get; set; }

    /// <summary>The editor acting; the server re-derives the trusted identity, so this is informational.</summary>
    [Parameter] public Guid ActingUserId { get; set; }

    [Parameter] public EventCallback OnClose { get; set; }

    [Inject]
    private IWallPanelService WallPanelService { get; set; } = default!;

    private bool _loading = true;
    private bool _busy;
    private string? _error;
    private int _createdCount;

    // Bumped whenever the compared panels change so each PanelImageView re-fits its view.
    private int _focusKey;

    private List<WallPanelInfo> _livePanels = [];
    private Guid _leftPanelId;
    private Guid _rightPanelId;
    private IReadOnlyList<PanelHold> _leftHolds = [];
    private IReadOnlyList<PanelHold> _rightHolds = [];
    private Guid? _leftHoldId;
    private Guid? _rightHoldId;

    // The endpoints of an existing link the user is previewing (hover/Show), one per side, plus the
    // link's 1-based number so both endpoints carry the same badge to read as one pair.
    private Guid? _previewLeftId;
    private Guid? _previewRightId;
    private int _previewNumber;

    // Every hold link on the wall; filtered to the two shown panels for the break-link list.
    private List<HoldLinkPair> _links = [];

    protected override async Task OnInitializedAsync()
    {
        var panels = await WallPanelService.GetPanelsAsync(WallId);
        _livePanels = panels.Where(p => p.IsLive).ToList();
        _links = (await WallPanelService.GetHoldLinksAsync(WallId)).ToList();

        if (_livePanels.Count >= 2)
        {
            var (left, right) = PickInitialPair();
            _leftPanelId = left;
            _rightPanelId = right;
            await LoadPanelHoldsAsync();
        }

        _loading = false;
    }

    // Two live panels: skip the picker entirely. More than two: default to an obviously adjacent
    // pair if there is one, else just the first two — the user can re-pick either side.
    private (Guid Left, Guid Right) PickInitialPair()
    {
        var first = _livePanels[0];
        var adjacent = _livePanels.Skip(1).FirstOrDefault(p => IsAdjacent(first, p));
        var second = adjacent ?? _livePanels[1];
        return (first.Id, second.Id);
    }

    private static bool IsAdjacent(WallPanelInfo a, WallPanelInfo b) =>
        (Math.Abs(a.Col - b.Col) == 1 && a.Row == b.Row)
        || (Math.Abs(a.Row - b.Row) == 1 && a.Col == b.Col);

    private async Task LoadPanelHoldsAsync()
    {
        _leftHolds = await WallPanelService.GetPanelHoldsAsync(WallId, _leftPanelId, includeStaged: false);
        _rightHolds = await WallPanelService.GetPanelHoldsAsync(WallId, _rightPanelId, includeStaged: false);
        _leftHoldId = null;
        _rightHoldId = null;
        ClearPreview();
        _focusKey++;
    }

    private async Task OnLeftPanelChange(ChangeEventArgs e)
    {
        if (Guid.TryParse(e.Value?.ToString(), out var id) && id != _rightPanelId)
        {
            _leftPanelId = id;
            _error = null;
            await LoadPanelHoldsAsync();
        }
    }

    private async Task OnRightPanelChange(ChangeEventArgs e)
    {
        if (Guid.TryParse(e.Value?.ToString(), out var id) && id != _leftPanelId)
        {
            _rightPanelId = id;
            _error = null;
            await LoadPanelHoldsAsync();
        }
    }

    private void OnLeftTap(Guid holdId)
    {
        _leftHoldId = holdId;
        _error = null;
    }

    private void OnRightTap(Guid holdId)
    {
        _rightHoldId = holdId;
        _error = null;
    }

    private void ClearSelection()
    {
        _leftHoldId = null;
        _rightHoldId = null;
        _error = null;
    }

    private async Task LinkHolds()
    {
        if (_leftHoldId is not { } left || _rightHoldId is not { } right)
        {
            return;
        }

        _busy = true;
        _error = null;
        try
        {
            await WallPanelService.CreateHoldLinkAsync(WallId, left, right);
            _links = (await WallPanelService.GetHoldLinksAsync(WallId)).ToList();
            _createdCount++;
            _leftHoldId = null;
            _rightHoldId = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task BreakLink(HoldLinkPair link)
    {
        _busy = true;
        _error = null;
        try
        {
            await WallPanelService.DeleteHoldLinkAsync(WallId, link.HoldAId, link.HoldBId);
            _links = (await WallPanelService.GetHoldLinksAsync(WallId)).ToList();
            ClearPreview();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    // The wall links whose two endpoints fall one on each of the two shown panels (either order).
    private List<HoldLinkPair> ExistingLinksBetween()
    {
        var leftIds = _leftHolds.Select(h => h.Id).ToHashSet();
        var rightIds = _rightHolds.Select(h => h.Id).ToHashSet();
        return _links
            .Where(l => (leftIds.Contains(l.HoldAId) && rightIds.Contains(l.HoldBId))
                || (leftIds.Contains(l.HoldBId) && rightIds.Contains(l.HoldAId)))
            .ToList();
    }

    private void PreviewLink(HoldLinkPair link, int number)
    {
        _previewNumber = number;
        var leftIds = _leftHolds.Select(h => h.Id).ToHashSet();
        if (leftIds.Contains(link.HoldAId))
        {
            _previewLeftId = link.HoldAId;
            _previewRightId = link.HoldBId;
        }
        else
        {
            _previewLeftId = link.HoldBId;
            _previewRightId = link.HoldAId;
        }
    }

    private void ClearPreview()
    {
        _previewLeftId = null;
        _previewRightId = null;
        _previewNumber = 0;
    }

    private string LeftCaption => PanelLabel(_leftPanelId, "Left panel");
    private string RightCaption => PanelLabel(_rightPanelId, "Right panel");

    private string PanelLabel(Guid id, string side)
    {
        var p = _livePanels.FirstOrDefault(x => x.Id == id);
        return p is null ? side : $"{side} ({p.Col}, {p.Row}) — tap a hold";
    }

    private string PhotoUrl(Guid panelId) => $"/api/walls/{WallId}/panels/{panelId}/photo";

    private Task Close() => OnClose.InvokeAsync();
}
