// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The hub's wall-wake observers (<see cref="IWallFeedHub.ObserveWakes"/>): told about every wake the hub drains, of any
/// wall, subscribed or not. A wake says only "this wall has new rows"; it carries no rows and needs no viewer.
/// </summary>
internal sealed class FeedWakeObservers(ILogger logger)
{
    private readonly ConcurrentDictionary<object, Action<Guid>> observers = new();

    public IDisposable Add(Action<Guid> observer)
    {
        var key = new object();
        observers[key] = observer;
        return new FeedWakeObservation(() => observers.TryRemove(key, out _));
    }

    // On the hub's loop: an observer must not block, and one that throws is logged and skipped.
    public void Notify(Guid wallId)
    {
        foreach (var observer in observers.Values)
        {
            try
            {
                observer(wallId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A feed wake observer threw on wall {WallId}", wallId);
            }
        }
    }
}
