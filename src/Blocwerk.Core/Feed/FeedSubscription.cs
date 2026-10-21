// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>An open subscription to one wall; dispose it to stop the deliveries.</summary>
public sealed class FeedSubscription : IDisposable
{
    private readonly Action unsubscribe;
    private int disposed;

    internal FeedSubscription(Guid wallId, FeedCursor start, Action unsubscribe)
    {
        WallId = wallId;
        Start = start;
        this.unsubscribe = unsubscribe;
    }

    public Guid WallId { get; }

    /// <summary>Gets the hub's cursor at subscription: deliveries carry only rows after it.</summary>
    public FeedCursor Start { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            unsubscribe();
        }
    }
}
