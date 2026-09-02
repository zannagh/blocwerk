using System.Collections.Concurrent;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.State;

/// <summary>
/// Per-circuit cache of the expensive wall/boulder aggregate reads. Registered scoped, so it lives
/// for the lifetime of one Blazor circuit and is never shared between users. Without it every
/// navigation rebuilds a <c>DbContext</c> and re-runs the wall/boulder queries (one wall view is
/// several DB round-trips); with it a revisit is served from memory.
///
/// Coherence comes from <see cref="IDomainChangeNotifier"/>: when any circuit (or a background
/// service) mutates a wall/boulder, the matching entries here are evicted and <see cref="Changed"/>
/// is raised so an open view can live-refresh — which also means one climber's edit shows up for
/// everyone currently looking at that wall/boulder. Mirrors the <see cref="SessionState"/> pattern.
/// </summary>
public sealed class WallCacheState : IDisposable
{
    private readonly IWallService walls;
    private readonly IBoulderService boulders;
    private readonly IDomainChangeNotifier notifier;

    // Cached tasks (not values) so concurrent callers for the same key share a single query and
    // the "5 round-trips per wall view" collapse into deduplicated loads.
    private readonly ConcurrentDictionary<Guid, Task<Wall?>> wallById = new();
    private readonly ConcurrentDictionary<Guid, Task<Boulder?>> boulderById = new();
    private readonly ConcurrentDictionary<(Guid Wall, int Generation), Task<List<Hold>>> holdsByGeneration = new();
    private readonly object myWallsGate = new();
    private Task<List<Wall>>? myWalls;

    public WallCacheState(IWallService walls, IBoulderService boulders, IDomainChangeNotifier notifier)
    {
        this.walls = walls;
        this.boulders = boulders;
        this.notifier = notifier;
        notifier.Changed += OnDomainChanged;
    }

    /// <summary>
    /// Raised (on the publishing thread) after a change evicts cache entries. Components filter by
    /// the ids they show and marshal a reload via <c>InvokeAsync</c>.
    /// </summary>
    public event Action<DomainChange>? Changed;

    public Task<Wall?> GetWallAsync(Guid wallId) =>
        GetOrLoad(wallById, wallId, () => walls.GetWallAsync(wallId));

    public Task<Boulder?> GetBoulderAsync(Guid boulderId) =>
        GetOrLoad(boulderById, boulderId, () => boulders.GetBoulderAsync(boulderId));

    public Task<List<Hold>> GetHoldsForGenerationAsync(Guid wallId, int generation) =>
        GetOrLoad(holdsByGeneration, (wallId, generation), () => walls.GetHoldsForGenerationAsync(wallId, generation));

    public Task<List<Wall>> GetMyWallsAsync()
    {
        lock (myWallsGate)
        {
            return myWalls ??= LoadMyWalls();
        }
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
            lock (myWallsGate)
            {
                myWalls = null;
            }

            throw;
        }
    }

    private static Task<T> GetOrLoad<TKey, T>(
        ConcurrentDictionary<TKey, Task<T>> cache, TKey key, Func<Task<T>> load)
        where TKey : notnull =>
        cache.GetOrAdd(key, _ => LoadOrEvict(cache, key, load));

    private static async Task<T> LoadOrEvict<TKey, T>(
        ConcurrentDictionary<TKey, Task<T>> cache, TKey key, Func<Task<T>> load)
        where TKey : notnull
    {
        try
        {
            return await load();
        }
        catch
        {
            cache.TryRemove(key, out _);
            throw;
        }
    }

    private void OnDomainChanged(DomainChange change)
    {
        switch (change.Scope)
        {
            case DomainChangeScope.WallList:
                lock (myWallsGate)
                {
                    myWalls = null;
                }

                break;

            case DomainChangeScope.Wall:
                EvictWall(change.WallId);
                break;

            case DomainChangeScope.Boulder:
                // Evict the boulder, then its wall aggregate — falling back to the boulder's own
                // cached WallId when the change didn't carry one (see the interceptor's BoulderHold case).
                var wallId = change.WallId;
                if (boulderById.TryRemove(change.BoulderId, out var removed) &&
                    wallId == Guid.Empty && removed.IsCompletedSuccessfully)
                {
                    wallId = removed.Result?.WallId ?? Guid.Empty;
                }

                EvictWall(wallId);
                break;
        }

        Changed?.Invoke(change);
    }

    private void EvictWall(Guid wallId)
    {
        if (wallId == Guid.Empty)
        {
            return;
        }

        wallById.TryRemove(wallId, out _);

        foreach (var key in holdsByGeneration.Keys.Where(key => key.Wall == wallId))
        {
            holdsByGeneration.TryRemove(key, out _);
        }

        // The wall aggregate carries its boulders, so a wall change invalidates any cached boulder
        // that belongs to it too.
        foreach (var entry in boulderById.Where(entry => entry.Value.IsCompletedSuccessfully && entry.Value.Result?.WallId == wallId))
        {
            boulderById.TryRemove(entry.Key, out _);
        }
    }

    public void Dispose()
    {
        notifier.Changed -= OnDomainChanged;
    }
}
