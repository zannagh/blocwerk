using Blocwerk.Core.Capture;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The hand-off from the capture section (Wall Shape card) to where panel photos are managed (Wall
/// Settings card): a capture photo staged as a new panel opens the panel grid's normal overlap review,
/// and capture photos picked for a wall update open the normal update flow with them pre-filled.
/// </summary>
public partial class WallDetail
{
    private Guid _panelGridKey = Guid.NewGuid();
    private Guid? _resumePanelId;
    private IReadOnlyList<CaptureUpdatePhoto>? _bigUpdatePrefill;

    private async Task OnCapturePanelStaged(Guid panelId)
    {
        // A fresh key remounts the grid, which re-reads the panels and opens this staged panel's review.
        _resumePanelId = panelId;
        _panelGridKey = Guid.NewGuid();
        StateHasChanged();
        await RevealPanelsAsync();
    }

    private async Task OnCaptureWallUpdate(IReadOnlyList<CaptureUpdatePhoto> photos)
    {
        _bigUpdatePrefill = photos;
        OpenBigUpdate();
        StateHasChanged();
        await RevealPanelsAsync();
    }

    private async Task RevealPanelsAsync()
    {
        try
        {
            await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "/js/reveal-section.js");
            await module.InvokeVoidAsync("reveal", "wall-panels");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException)
        {
            Logger.LogDebug(ex, "Could not reveal the panel section of wall {WallId}", WallId);
        }
    }
}
