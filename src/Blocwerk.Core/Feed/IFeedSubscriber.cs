// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>
/// Receives a wall's deliveries. <see cref="OnFeed"/> is called synchronously on the hub's single loop thread while the
/// hub holds the wall's gate, so a slow handler delays every wall's delivery and blocks subscribes to its wall.
/// <list type="bullet">
/// <item><description>Do not block and do not await: evict, raise an event and return. Hand UI work off with the
/// component's <c>InvokeAsync</c> (never wait for it) and never touch cached entities in place.</description></item>
/// <item><description>Tolerate a call that arrives after the <see cref="FeedSubscription"/> was disposed: a delivery
/// already being handed out when <c>Dispose</c> runs still reaches this subscriber, at most once more.</description></item>
/// <item><description>An exception is logged and the subscriber misses that batch; it is not unsubscribed.</description></item>
/// </list>
/// </summary>
public interface IFeedSubscriber
{
    void OnFeed(FeedDelivery delivery);
}
