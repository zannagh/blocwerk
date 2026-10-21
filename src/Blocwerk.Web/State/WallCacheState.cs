// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
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
/// <b>Coherence, two paths over the same feed rows.</b>
/// <list type="bullet">
/// <item><description>Own writes, synchronously (<see cref="FeedCommits"/>): every commit of this process evicts here
/// before the save (or the JS→API request) returns, so a page that saves and reloads straight away reads fresh data. The
/// eviction is unconditional, and it also keeps the wall list (<see cref="WallListCache"/>) and the kept "no access"
/// answers current.</description></item>
/// <item><description>The live feed (<see cref="WallFeedLink"/>, design §5.5): every wall with a cached entry is
/// subscribed to the hub, and each event evicts only what it touches (<see cref="WallFeedEviction"/>). Every load is
/// tagged with its <c>CursorAtLoad</c>, so an event the load already reflects (a delivery of a commit the first path
/// already evicted for) evicts nothing. The feed also handles access: a removed member leaves the wall
/// (<see cref="AccessLost"/>), a demoted or promoted one resubscribes.</description></item>
/// </list>
/// </para>
/// <para>
/// <see cref="Changed"/> is raised only when a change actually evicted something, so the event of a change the page
/// already reloaded for costs no second reload.
/// </para>
/// </summary>
public sealed class WallCacheState : IDisposable
{
    private readonly IWallService walls;
    private readonly IBoulderService boulders;
    private readonly WallFeedLink link;
    private readonly ILogger<WallCacheState> logger;
    private readonly IDisposable commitObservation;
    private readonly WallCacheStore store = new();
    private readonly WallFeedWatches watches = new();
    private readonly WallListCache wallList = new();
    private int disposed;

    public WallCacheState(
        IWallService walls, IBoulderService boulders, FeedCommits commits, WallFeedLink link, ILogger<WallCacheState> logger)
    {
        this.walls = walls;
        this.boulders = boulders;
        this.link = link;
        this.logger = logger;
        commitObservation = commits.Observe(OnCommitted);
        link.Delivered += OnDelivered;
        link.ViewerChanged += OnViewerChanged;
        link.AccessLost += OnAccessLost;
    }

    /// <summary>
    /// Raised (on the writer's thread for an own write, on the feed hub's loop for a delivery) after a change evicted cache
    /// entries. Components filter by the ids they show and marshal a reload via <c>InvokeAsync</c>, never awaited. Each
    /// handler is called on its own: one that throws is logged and never stops the other handlers or the evictions.
    /// </summary>
    public event Action<WallCacheChange>? Changed;

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

    /// <summary>The circuit's viewer on a live, unrevoked subscription of the wall, or null (job pulses, PR 1.7).</summary>
    public FeedViewer? ActiveViewerOf(Guid wallId) => link.ActiveViewerOf(wallId);

    /// <summary>Keeps the wall's subscription while a live list follows it, even when nothing of it is cached.</summary>
    public IDisposable Watch(Guid wallId) => watches.Watch(wallId);

    public Task<List<Wall>> GetMyWallsAsync() => wallList.GetAsync(walls.GetMyWallsAsync);

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

        commitObservation.Dispose();
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

    // On the writer's thread, right after the commit: evict, raise, return. Never await here. The envelopes carry
    // FeedCommit.Cursor, so every cached entry they touch goes; access changes are left to the audience-filtered feed.
    private void OnCommitted(FeedCommit commit)
    {
        if (Volatile.Read(ref disposed) == 1)
        {
            return;
        }

        // The list and the denied answers first: they touch no handler until the list's own raise.
        if (WallListCache.GrantsWalls(commit))
        {
            link.ForgetDenied(); // a wall the user joined may now be subscribed to
        }

        var listStale = wallList.IsStaleAfter(commit) && wallList.Forget();
        foreach (var wall in commit.Events.Select(e => e.Envelope).Where(e => e.WallId is not null).GroupBy(e => e.WallId!.Value))
        {
            try
            {
                Evict(wall.Key, wall, resync: false);
            }
            catch (Exception ex)
            {
                // Never reached by a handler (Raise isolates them); a fault in the eviction itself drops the wall whole.
                logger.LogError(ex, "Own-write eviction of wall {WallId} failed; the wall is evicted whole", wall.Key);
                store.EvictWall(wall.Key);
            }
        }

        if (listStale)
        {
            Raise(WallCacheChange.WallList);
        }
    }

    // On the hub's loop: evict, raise, return. Never await here.
    private void OnDelivered(FeedDelivery delivery)
    {
        if (Volatile.Read(ref disposed) == 1)
        {
            return;
        }

        Evict(delivery.WallId, delivery.Events, delivery.Resync);
        FeedDelivered?.Invoke(delivery);
    }

    private void Evict(Guid wallId, IEnumerable<FeedEnvelope> events, bool resync)
    {
        var wall = resync && store.EvictWall(wallId);
        var changedBoulders = new HashSet<Guid>();
        var inFlight = new List<Task<Guid?>>();
        foreach (var e in events)
        {
            var evicted = WallFeedEviction.Apply(store, wallId, e);
            wall |= evicted.Wall;
            changedBoulders.UnionWith(evicted.Boulders);
            inFlight.AddRange(evicted.InFlight);
        }

        RaiseFeedChange(wallId, wall, changedBoulders);
        if (!wall)
        {
            // Reported once the load tells its wall, and only for this wall.
            WallFeedEvicted.WhenLoaded(inFlight, id =>
            {
                if (!changedBoulders.Contains(id) && Volatile.Read(ref disposed) == 0)
                {
                    Raise(WallCacheChange.OfBoulder(wallId, id));
                }
            });
        }
    }

    private void RaiseFeedChange(Guid wallId, bool wall, IEnumerable<Guid> changedBoulders)
    {
        if (wall)
        {
            Raise(WallCacheChange.OfWall(wallId));
            return;
        }

        foreach (var boulderId in changedBoulders)
        {
            Raise(WallCacheChange.OfBoulder(wallId, boulderId));
        }
    }

    // One handler at a time, so a component that throws (a disposed page, a bug) costs only its own reload.
    private void Raise(WallCacheChange change)
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<WallCacheChange>)handler)(change);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A wall cache Changed handler threw on {Change}", change);
            }
        }
    }

    // Other audiences can load other things (drafts): reload the wall.
    private void OnViewerChanged(Guid wallId)
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            store.EvictWall(wallId);
            Raise(WallCacheChange.OfWall(wallId));
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
        wallList.Forget();
        Raise(WallCacheChange.WallList);
        Raise(WallCacheChange.OfWall(wallId));
        AccessLost?.Invoke(wallId);
    }
}
