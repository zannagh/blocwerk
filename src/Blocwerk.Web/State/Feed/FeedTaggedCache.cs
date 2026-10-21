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
    public Task<CacheLoad<T>> GetOrLoad(TKey key, Func<Task<CacheLoad<T>>> load) =>
        entries.GetOrAdd(key, k => LoadOrDrop(k, load));

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
    /// Removes every entry whose key or finished value matches <paramref name="match"/> (a load in flight matches when its
    /// key does). Returns the removed keys.
    /// </summary>
    public List<TKey> EvictWhere(Func<TKey, T?, bool> match)
    {
        var removed = new List<TKey>();
        foreach (var (key, task) in entries)
        {
            var value = task.IsCompletedSuccessfully ? task.Result.Value : default;
            if (match(key, value) && entries.TryRemove(new KeyValuePair<TKey, Task<CacheLoad<T>>>(key, task)))
            {
                removed.Add(key);
            }
        }

        return removed;
    }

    private async Task<CacheLoad<T>> LoadOrDrop(TKey key, Func<Task<CacheLoad<T>>> load)
    {
        try
        {
            return await load();
        }
        catch
        {
            entries.TryRemove(key, out _);
            throw;
        }
    }
}
