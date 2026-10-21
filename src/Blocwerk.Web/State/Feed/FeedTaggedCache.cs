// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Feed;

/// <summary>
/// A per-circuit map of deduplicated loads, each tagged with its <c>CursorAtLoad</c>. Concurrent callers for one key share
/// one query; a failed load is dropped so the next call retries. Eviction never touches cached values in place (the
/// clone-before-edit rule): it only removes entries, so a component holding a value keeps it.
/// </summary>
/// <typeparam name="TKey">The key.</typeparam>
/// <typeparam name="T">The cached value.</typeparam>
public sealed class FeedTaggedCache<TKey, T>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Task<CacheLoad<T>>> entries = new();

    public IEnumerable<TKey> Keys => entries.Keys;

    /// <summary>The cached load of <paramref name="key"/>, or a new one that <paramref name="load"/> runs.</summary>
    public Task<CacheLoad<T>> GetOrLoad(TKey key, Func<Task<CacheLoad<T>>> load)
    {
        while (true)
        {
            if (entries.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var pending = new TaskCompletionSource<CacheLoad<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (entries.TryAdd(key, pending.Task))
            {
                _ = RunAsync(key, pending, load);
                return pending.Task;
            }
        }
    }

    /// <summary>The finished value of <paramref name="key"/>, if it is cached and loaded.</summary>
    public bool TryPeek(TKey key, out CacheLoad<T> load)
    {
        if (entries.TryGetValue(key, out var task) && task.IsCompletedSuccessfully)
        {
            load = task.Result;
            return true;
        }

        load = default!;
        return false;
    }

    /// <summary>Removes <paramref name="key"/> unconditionally; true when something was cached.</summary>
    public bool Evict(TKey key) => entries.TryRemove(key, out _);

    /// <summary>
    /// Removes <paramref name="key"/> unless its load already reflects the event at <paramref name="cursor"/>. A load still
    /// in flight is removed too: whether it saw the event is unknown. True when something was removed.
    /// </summary>
    public bool EvictFor(TKey key, FeedCursor cursor)
    {
        if (!entries.TryGetValue(key, out var task))
        {
            return false;
        }

        if (task.IsCompletedSuccessfully && !task.Result.Predates(cursor))
        {
            return false;
        }

        return entries.TryRemove(new KeyValuePair<TKey, Task<CacheLoad<T>>>(key, task));
    }

    /// <summary>
    /// Removes every finished entry whose key and value match <paramref name="match"/>, and every load still in flight
    /// whose key matches <paramref name="inFlight"/> (default: every one): what an unfinished load will hold is unknown,
    /// so it may belong to the evicted scope and may predate the event. Returns the removed keys.
    /// </summary>
    public List<TKey> EvictWhere(Func<TKey, T, bool> match, Func<TKey, bool>? inFlight = null)
    {
        var removed = new List<TKey>();
        foreach (var (key, task) in entries)
        {
            var hit = task.IsCompletedSuccessfully ? match(key, task.Result.Value) : inFlight?.Invoke(key) ?? true;
            if (hit && entries.TryRemove(new KeyValuePair<TKey, Task<CacheLoad<T>>>(key, task)))
            {
                removed.Add(key);
            }
        }

        return removed;
    }

    /// <summary>
    /// Removes every finished entry that matches and every load still in flight; returns the finished keys, and the
    /// in-flight loads in <paramref name="inFlight"/>, whose values tell later whether they were in scope.
    /// </summary>
    public List<TKey> EvictWhere(Func<TKey, T, bool> match, out List<Task<CacheLoad<T>>> inFlight)
    {
        var removed = new List<TKey>();
        inFlight = [];
        foreach (var (key, task) in entries)
        {
            var finished = task.IsCompletedSuccessfully;
            if ((!finished || match(key, task.Result.Value)) && entries.TryRemove(new KeyValuePair<TKey, Task<CacheLoad<T>>>(key, task)))
            {
                if (finished)
                {
                    removed.Add(key);
                }
                else
                {
                    inFlight.Add(task);
                }
            }
        }

        return removed;
    }

    /// <summary>True when any entry matches: a finished one by key and value, one in flight by key.</summary>
    public bool Any(Func<TKey, T, bool> match, Func<TKey, bool> inFlight) =>
        entries.Any(e => e.Value.IsCompletedSuccessfully ? match(e.Key, e.Value.Result.Value) : inFlight(e.Key));

    // A failed load is dropped by identity, so it can never take a newer entry of the same key with it.
    private async Task RunAsync(TKey key, TaskCompletionSource<CacheLoad<T>> pending, Func<Task<CacheLoad<T>>> load)
    {
        try
        {
            pending.SetResult(await load());
        }
        catch (Exception ex)
        {
            entries.TryRemove(new KeyValuePair<TKey, Task<CacheLoad<T>>>(key, pending.Task));
            pending.SetException(ex);
        }
    }
}
