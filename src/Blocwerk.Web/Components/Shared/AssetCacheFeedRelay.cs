// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;
using Blocwerk.Web.State;
using Blocwerk.Web.State.Feed;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Hands the circuit's live feed to the page's device cache (Phase 3 PR 3.3): every delivery of a wall the circuit
/// follows that changes a data document or the manifest calls <c>bwAssetData.changed(wallId, manifest)</c>
/// (wwwroot/js/asset-data.js), which debounces 10 s and refetches the docs index (and the manifest first). Renders
/// nothing; mounted once in the layout. The call returns at once and every interop failure is absorbed: the device
/// cache only follows, the next page start catches up.
/// </summary>
public sealed class AssetCacheFeedRelay : ComponentBase, IDisposable
{
    private bool subscribed;
    private int disposed;

    [Inject]
    private WallCacheState WallCache { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        if (subscribed)
        {
            WallCache.FeedDelivered -= OnDelivered;
        }
    }

    // Interactive only: a prerender has no JS to call.
    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender && !subscribed && Volatile.Read(ref disposed) == 0)
        {
            subscribed = true;
            WallCache.FeedDelivered += OnDelivered;
        }
    }

    // On the hub's loop: classify, hand off, return. Never await here.
    // A delivery already in flight when the circuit went may still arrive: it is dropped.
    private void OnDelivered(FeedDelivery delivery)
    {
        if (Volatile.Read(ref disposed) == 1)
        {
            return;
        }

        var signal = AssetCacheSignals.Of(delivery);
        if (signal == AssetCacheSignal.None)
        {
            return;
        }

        _ = InvokeAsync(() => NotifyAsync(delivery.WallId, signal == AssetCacheSignal.Manifest));
    }

    private async Task NotifyAsync(Guid wallId, bool manifest)
    {
        try
        {
            await JS.InvokeVoidAsync("bwAssetData.changed", wallId, manifest);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            // An old cached script without bwAssetData, a closing circuit: the next start refreshes anyway.
        }
    }
}
