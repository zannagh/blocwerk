// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Full screen: while open, the host element is moved to <c>&lt;body&gt;</c> (wwwroot/js/wall3d-portal.js) so no page
/// stacking context can cover it, and moved back when it closes or the stage goes away.
/// </summary>
public partial class Wall3DStage
{
    /// <summary>Called from wwwroot/js/wall3d-portal.js when Escape asks to leave full screen.</summary>
    [JSInvokable]
    public Task CloseFromJs()
    {
        return InvokeAsync(() => SetFullscreen(false));
    }

    private async Task SyncPortalAsync()
    {
        if (portaled == fullscreen)
        {
            return;
        }

        portaled = fullscreen;
        try
        {
            portal ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/wall3d-portal.js");
            selfRef ??= DotNetObjectReference.Create(this);
            if (fullscreen)
            {
                await portal.InvokeVoidAsync("enter", host, selfRef);
            }
            else
            {
                await portal.InvokeVoidAsync("exit", host);
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or OperationCanceledException)
        {
            // Circuit gone or the browser did not answer: the fixed-position fallback still shows.
        }
    }

    private async Task ReleasePortalAsync()
    {
        if (portal is null)
        {
            return;
        }

        try
        {
            // The shell is leaving the page: a host still on <body> has to go with it.
            await portal.InvokeVoidAsync("exit", host);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or OperationCanceledException or InvalidOperationException)
        {
            // Nothing left to clean up.
        }

        await DisposeModuleAsync(portal);
    }
}
