// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The in-process own-write path (PR 1.8). <see cref="FeedInterceptor"/> hands every batch of committed feed rows to the
/// observers synchronously, right after the commit and before it wakes the hub: after a plain <c>SaveChanges</c> before
/// it returns, inside an explicit transaction on its commit, never after a rollback. The rows of a bulk site
/// (<see cref="FeedBulkWrite"/>) follow the same rule.
/// <para>
/// A circuit's cache evicts here, so a page that saves and reloads straight away (from a Blazor handler or after a JS→API
/// write) reads fresh data, which the hub's delivery after the commit, on its own loop, cannot promise. The hub's later
/// delivery of the same rows is then skipped by the reload's <c>CursorAtLoad</c>.
/// </para>
/// <para>
/// Observers see every row of every wall and every audience, User rows included, but only as actor-free events
/// (<see cref="FeedCommitEvent"/>): no actor id, no addressee id. They run inside the server and may only evict and raise
/// id-only events, never hand an event to a viewer. They run on the writer's thread, so they must
/// not block; one that throws is logged and skipped, and never fails the save. Single instance: a second app instance
/// would see these commits only through the feed (design §5.10).
/// </para>
/// </summary>
public sealed class FeedCommits(ILogger<FeedCommits> logger)
{
    private readonly ConcurrentDictionary<object, Action<FeedCommit>> observers = new();

    /// <summary>Gets a value indicating whether anyone observes; nothing is built for a commit nobody sees.</summary>
    public bool IsObserved => !observers.IsEmpty;

    /// <summary>Observes every commit until the returned handle is disposed.</summary>
    public IDisposable Observe(Action<FeedCommit> observer)
    {
        var key = new object();
        observers[key] = observer;
        return new FeedWakeObservation(() => observers.TryRemove(key, out _));
    }

    /// <summary>Hands the committed rows to every observer, on this thread. Never throws.</summary>
    internal void Publish(IReadOnlyList<WallChange> rows)
    {
        if (rows.Count == 0 || observers.IsEmpty)
        {
            return;
        }

        var commit = new FeedCommit(rows);
        foreach (var observer in observers.Values)
        {
            try
            {
                observer(commit);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A feed commit observer threw on {Count} committed row(s)", rows.Count);
            }
        }
    }
}
