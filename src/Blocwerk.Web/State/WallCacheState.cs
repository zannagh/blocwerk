// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Blocwerk.Web.State.Feed;

namespace Blocwerk.Web.State;

/// <summary>
/// Per-circuit cache of the expensive wall/boulder aggregate reads. Registered scoped, so it lives for the lifetime of one
/// Blazor circuit and is never shared between users. Without it every navigation rebuilds a <c>DbContext</c> and re-runs
/// the wall/boulder queries; with it a revisit is served from memory.
/// <para>
/// <b>Coherence, two paths side by side.</b>
/// <list type="bullet">
/// <item><description>The live feed (<see cref="WallFeedLink"/>, design §5.5): every wall with a cached entry is subscribed
/// to the hub, and each event evicts only what it touches (<see cref="WallFeedEviction"/>). Every load is tagged with its
/// <c>CursorAtLoad</c>, so an event the load already reflects evicts nothing. The feed also handles access: a removed
/// member leaves the wall (<see cref="AccessLost"/>), a demoted or promoted one resubscribes.</description></item>
/// <item><description><see cref="IDomainChangeNotifier"/>: synchronous, in-process, coarse. It stays until PR 1.8 because
/// it is the circuit's read-your-own-writes path: a page that saves and reloads straight away needs the eviction to
/// happen inside the save, which a feed delivery (after the commit, on the hub's loop) cannot promise. It is also the only
/// signal for the wall list.</description></item>
/// </list>
/// </para>
/// <para>
/// <see cref="Changed"/> is raised for a domain change, and for a feed event only when it actually evicted something, so
/// the event of a change the page already reloaded for costs no second reload.
/// </para>
/// </summary>
public sealed class WallCacheState : IDisposable
{
    private readonly IWallService walls;
    private readonly IBoulderService boulders;
    private readonly IDomainChangeNotifier notifier;
    private readonly WallFeedLink link;
    private readonly WallCacheStore store = new();
    private readonly WallFeedWatches watches = new();
    private readonly object myWallsGate = new();
    private Task<List<Wall>>? myWalls;
    private int disposed;

    public WallCacheState(IWallService walls, IBoulderService boulders, IDomainChangeNotifier notifier, WallFeedLink link)
    {
        this.walls = walls;
        this.boulders = boulders;
        this.notifier = notifier;
        this.link = link;
        notifier.Changed += OnDomainChanged;
        link.Delivered += OnDelivered;
        link.ViewerChanged += OnViewerChanged;
        link.AccessLost += OnAccessLost;
    }

    /// <summary>
    /// Raised (on the publishing thread, or the feed hub's loop) after a change evicts cache entries. Components filter by
    /// the ids they show and marshal a reload via <c>InvokeAsync</c>, never awaited.
    /// </summary>
    public event Action<DomainChange>? Changed;

    /// <summary>Raised (off the circuit's dispatcher) when the circuit lost its access to a wall: leave it.</summary>
    public event Action<Guid>? AccessLost;

    /// <summary>Raised on the hub's loop for every feed delivery, after eviction; for the live leaf lists (PR 1.5).</summary>
    public event Action<FeedDelivery>? FeedDelivered;

    /// <summary>
    /// Raised (off the circuit's dispatcher) after a resubscribe gave the circuit other audiences on a wall. Live lists
    /// re-read: other rows reach them now, and the revoked subscription dropped deliveries until the swap.
    /// </summary>
    public event Action<Guid>? ViewerChanged;

    internal WallCacheStore Store => store;

    public async Task<Wall?> GetWallAsync(Guid wallId) =>
        (await store.Walls.GetOrLoad(wallId, () => LoadOnWallAsync(wallId, () => walls.GetWallAsync(wallId)))).Value;

    public async Task<Boulder?> GetBoulderAsync(Guid boulderId) =>
        (await store.Boulders.GetOrLoad(boulderId, () => LoadBoulderAsync(boulderId))).Value;

    public async Task<List<Hold>> GetHoldsForGenerationAsync(Guid wallId, int generation) =>
        (await store.HoldSets.GetOrLoad(
            (wallId, generation),
            () => LoadOnWallAsync(wallId, () => walls.GetHoldsForGenerationAsync(wallId, generation)))).Value;

    /// <summary>The <c>CursorAtLoad</c> for a component's own load (a live list); null when the feed cannot be read.</summary>
    public Task<FeedCursor?> CursorAtLoadAsync() => link.CursorAtLoadAsync();

    /// <summary>The viewer the circuit is subscribed to the wall as; null without a subscription.</summary>
    public FeedViewer? ViewerOf(Guid wallId) => link.ViewerOf(wallId);

    /// <summary>Subscribes to the wall's feed if the circuit may see it (a live list on a page that caches nothing).</summary>
    public Task EnsureFeedAsync(Guid wallId) => link.EnsureAsync(wallId);

    /// <summary>Keeps the wall's subscription while a live list follows it, even when nothing of it is cached.</summary>
    public IDisposable Watch(Guid wallId) => watches.Watch(wallId);

    public Task<List<Wall>> GetMyWallsAsync()
    {
        lock (myWallsGate)
        {
            return myWalls ??= LoadMyWalls();
        }
    }

    /// <summary>
    /// Follows the page on screen (base-relative path): the wall of a member page is subscribed and pinned, so a removal
    /// reaches every page of it (editor, gallery, 3D, update procedure...), not only those that read this cache.
    /// </summary>
    public Task WatchRouteAsync(string relativePath)
    {
        var wallId = WallRoutes.MemberWallOf(relativePath);
        link.Pin(wallId);
        return wallId is { } id ? link.EnsureAsync(id) : Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        notifier.Changed -= OnDomainChanged;
        link.Delivered -= OnDelivered;
        link.ViewerChanged -= OnViewerChanged;
        link.AccessLost -= OnAccessLost;
    }

    // Subscribed first, so the cursor read next is at or after the subscription's start and no event falls in between.
    private async Task<CacheLoad<T>> LoadOnWallAsync<T>(Guid wallId, Func<Task<T>> load)
    {
        await SubscribeAsync(wallId);
        return await LoadTaggedAsync(load);
    }

    // Walls the circuit no longer caches anything of are unsubscribed whenever another wall subscribes, so subscriptions
    // do not pile up over a long session. A wall on screen reloads straight after an eviction, so it keeps its slot.
    private Task SubscribeAsync(Guid wallId)
    {
        link.Release(w => w == wallId || store.HasAnyOf(w) || watches.Has(w));
        return link.EnsureAsync(wallId);
    }

    // A boulder's wall is known only after the load. When that wall's subscription started after the load's cursor, the
    // events in between reach nobody, so the boulder is read once more under the subscription.
    private async Task<CacheLoad<Boulder?>> LoadBoulderAsync(Guid boulderId)
    {
        var load = await LoadTaggedAsync(() => boulders.GetBoulderAsync(boulderId));
        if (load.Value is { } boulder && !await link.CoversAsync(boulder.WallId, load))
        {
            load = await LoadTaggedAsync(() => boulders.GetBoulderAsync(boulderId));
        }

        return load;
    }

    private async Task<CacheLoad<T>> LoadTaggedAsync<T>(Func<Task<T>> load)
    {
        var at = await link.CursorAtLoadAsync();
        return new CacheLoad<T>(await load(), at);
    }

    private async Task<List<Wall>> LoadMyWalls()
    {
        try
        {
            return await walls.GetMyWallsAsync();
        }
        catch
        {
            // Don't cache a failed load — drop it so the next call retries.
            ForgetMyWalls();
            throw;
        }
    }

    private void ForgetMyWalls()
    {
        lock (myWallsGate)
        {
            myWalls = null;
        }
    }

    private void OnDomainChanged(DomainChange change)
    {
        switch (change.Scope)
        {
            case DomainChangeScope.WallList:
                ForgetMyWalls();
                link.ForgetDenied(); // a wall the user joined may now be subscribed to
                break;

            case DomainChangeScope.Wall:
                store.EvictWall(change.WallId);
                break;

            case DomainChangeScope.Boulder:
                // Evict the boulder, then its wall aggregate — falling back to the boulder's own cached WallId when the
                // change didn't carry one (see the interceptor's BoulderHold case).
                var wallId = change.WallId;
                if (store.Boulders.TryPeek(change.BoulderId, out var cached) && wallId == Guid.Empty)
                {
                    wallId = cached.Value?.WallId ?? Guid.Empty;
                }

                store.Boulders.Evict(change.BoulderId);
                store.EvictWall(wallId);
                break;
        }

        Changed?.Invoke(change);
    }

    // On the hub's loop: evict, raise, return. Never await here.
    private void OnDelivered(FeedDelivery delivery)
    {
        if (Volatile.Read(ref disposed) == 1)
        {
            return;
        }

        var wall = delivery.Resync && store.EvictWall(delivery.WallId);
        var changedBoulders = new HashSet<Guid>();
        var inFlight = new List<Task<Guid?>>();
        foreach (var e in delivery.Events)
        {
            var evicted = WallFeedEviction.Apply(store, delivery.WallId, e);
            wall |= evicted.Wall;
            changedBoulders.UnionWith(evicted.Boulders);
            inFlight.AddRange(evicted.InFlight);
        }

        RaiseFeedChange(delivery.WallId, wall, changedBoulders);
        if (!wall)
        {
            // Reported once the load tells its wall, and only for this wall.
            WallFeedEvicted.WhenLoaded(inFlight, id =>
            {
                if (!changedBoulders.Contains(id) && Volatile.Read(ref disposed) == 0)
                {
                    Changed?.Invoke(new DomainChange(DomainChangeScope.Boulder, delivery.WallId, id));
                }
            });
        }

        FeedDelivered?.Invoke(delivery);
    }

    private void RaiseFeedChange(Guid wallId, bool wall, IEnumerable<Guid> changedBoulders)
    {
        if (wall)
        {
            Changed?.Invoke(new DomainChange(DomainChangeScope.Wall, wallId, Guid.Empty));
            return;
        }

        foreach (var boulderId in changedBoulders)
        {
            Changed?.Invoke(new DomainChange(DomainChangeScope.Boulder, wallId, boulderId));
        }
    }

    // Other audiences can load other things (drafts): reload the wall.
    private void OnViewerChanged(Guid wallId)
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            store.EvictWall(wallId);
            Changed?.Invoke(new DomainChange(DomainChangeScope.Wall, wallId, Guid.Empty));
            ViewerChanged?.Invoke(wallId);
        }
    }

    private void OnAccessLost(Guid wallId)
    {
        if (Volatile.Read(ref disposed) == 1)
        {
            return;
        }

        store.EvictWall(wallId);
        ForgetMyWalls();
        Changed?.Invoke(new DomainChange(DomainChangeScope.WallList, Guid.Empty, Guid.Empty));
        Changed?.Invoke(new DomainChange(DomainChangeScope.Wall, wallId, Guid.Empty));
        AccessLost?.Invoke(wallId);
    }
}
