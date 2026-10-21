// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Live;

/// <summary>
/// One component's live view of its leaf list (design §5.5): it listens to the circuit's feed deliveries and access
/// changes through <see cref="WallCacheState"/>, selects its scope's events, and hands them to the component's dispatcher
/// without waiting (the hub's loop never blocks on UI work).
/// <para>
/// <b>Reloads.</b> A resync, a coarse <c>wall.resync</c>, an unknown payload schema, and a change of the circuit's viewer
/// on the wall (<see cref="WallCacheState.ViewerChanged"/>, <see cref="WallCacheState.AccessLost"/>) reach the list as a
/// reload: a resubscribe changes which rows reach it, and a revoked slot dropped deliveries until the swap.
/// </para>
/// <para>
/// <b>Holding.</b> While <c>isHeld</c> is true (the user is editing in the list), batches are merged, not applied, and
/// <see cref="ReleaseAsync"/> applies what accumulated once the edit ends. Nothing is lost and nothing is patched under the
/// user's hands.
/// </para>
/// <para>
/// <b>CursorAtLoad.</b> Every full load runs through <see cref="LoadAsync{T}"/>, which reads the feed cursor first. An
/// event at or below it is already in the list and is skipped; applying a newer one again is idempotent.
/// </para>
/// </summary>
/// <param name="cache">The circuit's cache, whose feed deliveries the list follows.</param>
/// <param name="scope">The list's wall, boulder and kinds.</param>
/// <param name="dispatch">The component's <c>InvokeAsync</c>.</param>
/// <param name="apply">Applies a batch on the component's dispatcher (patch, or reload when <see cref="LeafBatch.Reload"/>).</param>
/// <param name="isHeld">True while the user edits in the list; read on the dispatcher.</param>
public sealed class LeafFeed(
    WallCacheState cache, LeafScope scope, Func<Func<Task>, Task> dispatch, Func<LeafBatch, Task> apply, Func<bool>? isHeld = null)
    : IDisposable
{
    private readonly object gate = new();
    private FeedCursor? loadedAt;
    private LeafBatch? pending;
    private int disposed;

    public LeafScope Scope { get; } = scope;

    /// <summary>Opens a list's feed: it follows the circuit's deliveries from now on, until disposed.</summary>
    public static LeafFeed Open(
        WallCacheState cache, LeafScope scope, Func<Func<Task>, Task> dispatch, Func<LeafBatch, Task> apply, Func<bool>? isHeld = null)
    {
        var feed = new LeafFeed(cache, scope, dispatch, apply, isHeld);
        cache.FeedDelivered += feed.OnDelivered;
        cache.ViewerChanged += feed.OnViewerChanged;
        cache.AccessLost += feed.OnViewerChanged;
        _ = cache.EnsureFeedAsync(scope.WallId);
        return feed;
    }

    /// <summary>Gets the viewer the circuit is subscribed to the wall as; null without a subscription (share links).</summary>
    public FeedViewer? Viewer => cache.ViewerOf(Scope.WallId);

    private bool IsDisposed => Volatile.Read(ref disposed) == 1;

    /// <summary>
    /// Runs a full load of the list on the dispatcher, tagged with the feed cursor read before it. It supersedes what was
    /// held: those events were committed before the load starts.
    /// </summary>
    /// <typeparam name="T">The loaded value.</typeparam>
    public async Task<T> LoadAsync<T>(Func<Task<T>> load)
    {
        pending = null; // a load that starts now reflects every event already received
        var at = await cache.CursorAtLoadAsync();
        var value = await load();
        lock (gate)
        {
            loadedAt = at;
        }

        return value;
    }

    /// <summary>Applies what arrived while the list was held, unless it still is. Call on the dispatcher when an edit ends.</summary>
    public Task ReleaseAsync()
    {
        if (IsDisposed || isHeld?.Invoke() == true || pending is not { } batch)
        {
            return Task.CompletedTask;
        }

        pending = null;
        return apply(batch);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        cache.FeedDelivered -= OnDelivered;
        cache.ViewerChanged -= OnViewerChanged;
        cache.AccessLost -= OnViewerChanged;
    }

    // On the hub's loop: select, hand off, return.
    private void OnDelivered(FeedDelivery delivery)
    {
        FeedCursor? at;
        lock (gate)
        {
            at = loadedAt;
        }

        if (!IsDisposed && Scope.Select(delivery, at) is { } batch)
        {
            Post(batch);
        }
    }

    private void OnViewerChanged(Guid wallId)
    {
        if (!IsDisposed && wallId == Scope.WallId)
        {
            Post(LeafBatch.ReloadOnly);
        }
    }

    private void Post(LeafBatch batch) => _ = DispatchAsync(batch);

    private async Task DispatchAsync(LeafBatch batch)
    {
        try
        {
            await dispatch(() => ReceiveAsync(batch));
        }
        catch (Exception)
        {
            // The circuit is gone or the list failed to apply: it stays as it is, and the next load reads fresh.
        }
    }

    // On the dispatcher, so pending needs no lock.
    private Task ReceiveAsync(LeafBatch batch)
    {
        if (IsDisposed)
        {
            return Task.CompletedTask;
        }

        pending = pending?.Merge(batch) ?? batch;
        return ReleaseAsync();
    }
}
