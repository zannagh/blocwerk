// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Geometry.View3D;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The mobile-data notice (decision of 2026-10-21): on a phone or tablet, a wall whose 3D is not on the device yet asks
/// before anything 3D is downloaded, with the size from the wall's manifest. Continue starts it (and can be remembered
/// per user), Cancel leaves the view unloaded with a button to load it after all. bwAsset3d.gate decides; any failure to
/// ask means no notice, never a view that does not start.
/// </summary>
public partial class Wall3DStage
{
    private Wall3DNoticeState? notice;
    private bool declined;
    private bool noticePassed;

    /// <summary>Offer "Save 3D offline" in the tool column (not in small insets).</summary>
    [Parameter]
    public bool OfferSaveOffline { get; set; } = true;

    private async Task<bool> PassDataNoticeAsync(Wall3DView view)
    {
        if (noticePassed)
        {
            return true;
        }

        Wall3DDataGate? gate;
        try
        {
            gate = await JS.InvokeAsync<Wall3DDataGate?>("bwAsset3d.gate", view.WallId);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or OperationCanceledException)
        {
            gate = null;
        }

        if (gate is not { Ask: true })
        {
            noticePassed = true;
            return true;
        }

        var waiting = new Wall3DNoticeState(gate.Bytes, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        notice = waiting;
        await InvokeAsync(StateHasChanged);
        var go = await waiting.Answer.Task;
        notice = null;
        noticePassed = go;
        declined = !go && !disposed;
        if (!disposed)
        {
            await InvokeAsync(StateHasChanged);
        }

        return go;
    }

    private async Task AnswerNoticeAsync(bool go, bool remember)
    {
        if (notice is not { } waiting)
        {
            return;
        }

        if (go)
        {
            try
            {
                await JS.InvokeVoidAsync("bwAsset3d.accept", View.WallId, remember);
            }
            catch (Exception ex) when (ex is JSDisconnectedException or JSException or OperationCanceledException)
            {
                // Not remembered: the next view asks again.
            }
        }

        waiting.Answer.TrySetResult(go);
    }

    // "Load 3D" after a Cancel: mount the current view as if it were new.
    private void LoadAfterDecline()
    {
        declined = false;
        noticePassed = true;
        mounted = null;
        StateHasChanged();
    }
}
