// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Feed;

/// <summary>
/// One circuit's subscription to one wall, as one viewer. It is the hub's <see cref="IFeedSubscriber"/>: deliveries arrive
/// synchronously on the hub's loop under the wall's gate, so it only forwards them; a retired slot (disposed, replaced by
/// a resubscribe, or left) drops the one late delivery the hub may still hand it, and so does a revoked one: after an
/// <c>access.revoked</c> its audiences are no longer the viewer's, until a resubscribe replaces it.
/// </summary>
public sealed class WallFeedSlot(Guid wallId, FeedViewer viewer, Action<WallFeedSlot, FeedDelivery> receive) : IFeedSubscriber
{
    private FeedSubscription? subscription;
    private int retired;
    private int revoked;

    public Guid WallId { get; } = wallId;

    public FeedViewer Viewer { get; } = viewer;

    /// <summary>Gets the hub's cursor at subscription; null until the subscribe returned.</summary>
    public FeedCursor? Start => subscription?.Start;

    /// <summary>Gets or sets the epoch of the last delivery; another epoch means "reload the wall".</summary>
    public Guid? Epoch { get; set; }

    public bool IsRetired => Volatile.Read(ref retired) == 1;

    /// <summary>Gets a value indicating whether an access event made this slot's audiences stale.</summary>
    public bool IsRevoked => Volatile.Read(ref revoked) == 1;

    public void OnFeed(FeedDelivery delivery)
    {
        if (!IsRetired && !IsRevoked)
        {
            receive(this, delivery);
        }
    }

    /// <summary>Takes the subscription the hub returned; a slot retired meanwhile disposes it straight away.</summary>
    public void Attach(FeedSubscription opened)
    {
        subscription = opened;
        Epoch ??= opened.Start.Epoch;
        if (IsRetired)
        {
            opened.Dispose();
        }
    }

    /// <summary>
    /// Drops every later delivery, synchronously and without touching the hub: it runs inside the hub's fan-out, so the
    /// subscription itself is disposed later, by <see cref="Retire"/>.
    /// </summary>
    public void Revoke() => Volatile.Write(ref revoked, 1);

    /// <summary>Stops the deliveries (idempotent).</summary>
    public void Retire()
    {
        if (Interlocked.Exchange(ref retired, 1) == 0)
        {
            subscription?.Dispose();
        }
    }
}
