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
/// reload: a resubscribe changes which rows reach it, and a revoked slot dropped deliveries until the swap. Reloads are
/// coalesced for <see cref="ReloadDelay"/>, so an epoch change or an erasure resync does not stampede every open page.
/// </para>
/// <para>
/// <b>Holding.</b> While <c>isHeld</c> is true (the user is editing in the list), and while a load runs, batches are
/// merged, not applied. A load's end releases what arrived above its cursor; <see cref="ReleaseAsync"/> does when an edit
/// ends. Nothing is lost and nothing is patched under the user's hands or into a list a load is about to replace.
/// </para>
/// <para>
/// <b>CursorAtLoad.</b> Every full load runs through <see cref="LoadAsync{T}"/>: it subscribes first, reads the feed cursor,
/// then queries, so no event falls between the subscription and the load. An event at or below the cursor is already in
/// the list and is skipped. Only the latest of overlapping loads commits.
/// </para>
/// </summary>
/// <param name="cache">The circuit's cache, whose feed deliveries the list follows.</param>
/// <param name="scope">The list's wall, boulder and kinds.</param>
/// <param name="dispatch">The component's <c>InvokeAsync</c>.</param>
/// <param name="apply">Applies a batch on the component's dispatcher (patch, or reload when <see cref="LeafBatch.Reload"/>).</param>
/// <param name="isHeld">True while the user edits in the list; read on the dispatcher.</param>
/// <param name="logger">Where a failed hand-off is logged.</param>
public sealed class LeafFeed(
    WallCacheState cache,
    LeafScope scope,
    Func<Func<Task>, Task> dispatch,
    Func<LeafBatch, Task> apply,
    Func<bool>? isHeld,
    ILogger? logger) : IDisposable
{
    /// <summary>How long reload requests are gathered into one.</summary>
    public static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(300);

    private readonly object gate = new();
    private IDisposable? watch;
    private FeedCursor? loadedAt;
    private LeafBatch? pending;
    private int loads;
    private int loading;
    private int reloadQueued;
    private int disposed;

    public LeafScope Scope { get; } = scope;

    /// <summary>Gets the viewer the circuit is subscribed to the wall as; null without a subscription.</summary>
    public FeedViewer? Viewer => cache.ViewerOf(Scope.WallId);

    private bool IsDisposed => Volatile.Read(ref disposed) == 1;

    /// <summary>Opens a list's feed: it follows the circuit's deliveries from now on, until disposed.</summary>
    public static LeafFeed Open(
        WallCacheState cache,
        LeafScope scope,
        Func<Func<Task>, Task> dispatch,
        Func<LeafBatch, Task> apply,
        Func<bool>? isHeld = null,
        ILogger? logger = null)
    {
        var feed = new LeafFeed(cache, scope, dispatch, apply, isHeld, logger);
        feed.watch = cache.Watch(scope.WallId);
        cache.FeedDelivered += feed.OnDelivered;
        cache.ViewerChanged += feed.OnViewerChanged;
        cache.AccessLost += feed.OnViewerChanged;
        _ = cache.EnsureFeedAsync(scope.WallId); // started now; every load awaits it again before its cursor read
        return feed;
    }

    /// <summary>
    /// Runs a full load of the list on the dispatcher and hands its result to <paramref name="commit"/>, unless a newer load
    /// started meanwhile or the feed was disposed (the component moved on). Batches that arrive meanwhile are held; the
    /// ones above the load's cursor are applied after the commit. False when the result was dropped.
    /// </summary>
    /// <typeparam name="T">The loaded value.</typeparam>
    public async Task<bool> LoadAsync<T>(Func<Task<T>> load, Action<T> commit)
    {
        var generation = ++loads;
        loading++;
        var committed = false;
        FeedCursor? at = null;
        try
        {
            await cache.EnsureFeedAsync(Scope.WallId);
            at = await cache.CursorAtLoadAsync();
            var value = await load();
            if (!IsDisposed && generation == loads)
            {
                commit(value);
                lock (gate)
                {
                    loadedAt = at;
                }

                committed = true;
            }
        }
        finally
        {
            loading--;
            if (committed)
            {
                pending = pending?.After(at);
            }

            await ReleaseAsync();
        }

        return committed;
    }

    /// <summary>Applies what arrived while the list was held, unless it still is. Call on the dispatcher when an edit ends.</summary>
    public Task ReleaseAsync()
    {
        if (IsDisposed || loading > 0 || isHeld?.Invoke() == true || pending is not { } batch)
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

        watch?.Dispose();
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

    // A reload supersedes the events of its batch; a second reload within the delay joins the first.
    private void Post(LeafBatch batch)
    {
        if (!batch.Reload)
        {
            _ = DispatchAsync(batch);
        }
        else if (Interlocked.Exchange(ref reloadQueued, 1) == 0)
        {
            _ = ReloadLaterAsync();
        }
    }

    private async Task ReloadLaterAsync()
    {
        await Task.Delay(ReloadDelay);
        Volatile.Write(ref reloadQueued, 0);
        await DispatchAsync(LeafBatch.ReloadOnly);
    }

    private async Task DispatchAsync(LeafBatch batch)
    {
        try
        {
            await dispatch(() => ReceiveAsync(batch));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "A live list of wall {WallId} could not apply a feed batch; it stays as it is until its next load", Scope.WallId);
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
