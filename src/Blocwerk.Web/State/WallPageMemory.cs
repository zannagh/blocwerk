// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.State;

/// <summary>
/// The wall page as the circuit last showed it, so coming back to it (back from a boulder, above all) renders that page
/// at once while the page reloads it. Registered scoped: one per circuit, and a circuit is one identity (every identity
/// change in the app is a full page load), so nothing here is ever shown to anybody else.
/// <para>
/// It is a head start, never the truth: the page always reloads straight after restoring, and an entry is dropped when
/// the circuit loses access to its wall, when it gets old, and when more than <see cref="Capacity"/> walls were left.
/// </para>
/// </summary>
public sealed class WallPageMemory : IDisposable
{
    /// <summary>How many walls are remembered; the least recently left goes first.</summary>
    public const int Capacity = 2;

    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    private readonly WallCacheState cache;
    private readonly TimeProvider time;
    private readonly List<(Guid WallId, string? ShareToken, object Page, DateTimeOffset LeftAt)> entries = [];
    private readonly Lock gate = new();

    public WallPageMemory(WallCacheState cache, TimeProvider time)
    {
        this.cache = cache;
        this.time = time;
        cache.AccessLost += Forget;
    }

    /// <summary>Remembers the page of <paramref name="wallId"/> (through <paramref name="shareToken"/>, if any).</summary>
    public void Remember(Guid wallId, string? shareToken, object page)
    {
        lock (gate)
        {
            entries.RemoveAll(e => e.WallId == wallId && e.ShareToken == shareToken);
            entries.Add((wallId, shareToken, page, time.GetUtcNow()));
            if (entries.Count > Capacity)
            {
                entries.RemoveAt(0);
            }
        }
    }

    /// <summary>The page remembered for the wall, or null when there is none young enough.</summary>
    /// <typeparam name="T">The page's snapshot type, as the page remembered it.</typeparam>
    public T? Recall<T>(Guid wallId, string? shareToken)
        where T : class
    {
        lock (gate)
        {
            var index = entries.FindIndex(e => e.WallId == wallId && e.ShareToken == shareToken);
            if (index < 0)
            {
                return null;
            }

            if (time.GetUtcNow() - entries[index].LeftAt > MaxAge)
            {
                entries.RemoveAt(index);
                return null;
            }

            return entries[index].Page as T;
        }
    }

    /// <summary>Drops everything remembered of the wall.</summary>
    public void Forget(Guid wallId)
    {
        lock (gate)
        {
            entries.RemoveAll(e => e.WallId == wallId);
        }
    }

    public void Dispose()
    {
        cache.AccessLost -= Forget;
    }
}
