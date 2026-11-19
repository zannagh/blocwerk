// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// Whether the browser's landing renders the whole list under its cards: on the desktop tier only (browse-tier.js), so a
/// phone never renders a list it would hide. Off until the browser has said, and kept current as the window changes.
/// </summary>
public partial class WallDetail
{
    private bool desktopTier;
    private int? browseTierWatch;
    private DotNetObjectReference<WallDetail>? browseTierRef;

    /// <summary>Called by browse-tier.js with the tier now and on every change.</summary>
    /// <param name="desktop">True on the desktop tier.</param>
    /// <returns>A task that completes once the change is rendered.</returns>
    [JSInvokable]
    public Task SetDesktopTier(bool desktop)
    {
        if (desktopTier == desktop || _disposed)
        {
            return Task.CompletedTask;
        }

        desktopTier = desktop;
        return InvokeAsync(StateHasChanged);
    }

    private async Task WatchBrowseTierAsync()
    {
        if (browseTierWatch is not null || browseTierRef is not null)
        {
            return;
        }

        browseTierRef = DotNetObjectReference.Create(this);
        try
        {
            browseTierWatch = await JS.InvokeAsync<int>("bwBrowseTier.watch", browseTierRef);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or OperationCanceledException or JSException)
        {
            // No answer, no list under the cards: the phone layout, which is the safe one.
        }
    }

    private void StopBrowseTierWatch()
    {
        if (browseTierWatch is { } id)
        {
            _ = UnwatchBrowseTierAsync(id);
        }

        browseTierRef?.Dispose();
    }

    private async Task UnwatchBrowseTierAsync(int id)
    {
        try
        {
            await JS.InvokeVoidAsync("bwBrowseTier.unwatch", id);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or OperationCanceledException or JSException)
        {
            // A torn-down circuit has no watcher left to stop.
        }
    }
}
