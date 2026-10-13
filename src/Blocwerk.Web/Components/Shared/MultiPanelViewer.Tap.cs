// <copyright file="MultiPanelViewer.Tap.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The outlines-on-demand half of the panel viewer: the wall view shows the bare photo, hold-tap.js outlines the
/// hold that is tapped, and "Show all holds" brings the full overlay back on this device.
/// </summary>
public partial class MultiPanelViewer
{
    private bool showAllHolds;
    private bool showAllRead;

    // What the browser's hold list was last sent for, so a render that changes nothing does not resend it.
    private string? tapSentKey;

    /// <summary>
    /// When true (the wall view) the holds are not drawn until one is tapped; the tap is found and outlined in the
    /// browser. Off by default, so the boulder view keeps drawing exactly its highlighted holds.
    /// </summary>
    [Parameter]
    public bool OutlinesOnDemand { get; set; }

    /// <summary>Whether a hold that is nobody's highlight is drawn: always, unless outlines are on demand.</summary>
    private bool DrawsEveryHold => !OutlinesOnDemand || showAllHolds;

    private async Task ToggleShowAllHolds()
    {
        showAllHolds = !showAllHolds;
        try
        {
            await JS.InvokeVoidAsync("bwHoldTap.setShowAll", showAllHolds);
        }
        catch (JSException)
        {
            // The preference could not be stored; the choice still holds for this visit.
        }
        catch (JSDisconnectedException)
        {
            // Circuit gone; nothing to store for.
        }
    }

    /// <summary>Reads the device's "Show all holds" choice once, and hands the browser the current panel's holds.</summary>
    private async Task SyncHoldTapAsync()
    {
        if (!OutlinesOnDemand || _panel is null)
        {
            return;
        }

        if (!showAllRead)
        {
            showAllRead = true;
            if (await JS.InvokeAsync<bool>("bwHoldTap.showAll"))
            {
                showAllHolds = true;
                StateHasChanged();
            }
        }

        var key = $"{_panel.Id}:{_holds.Count}:{UsageCounts?.Count}";
        if (key == tapSentKey)
        {
            return;
        }

        tapSentKey = key;
        await JS.InvokeVoidAsync("bwHoldTap.setHolds", _viewportRef, HoldTapList.Build(_holds, UsageCounts));
    }
}
