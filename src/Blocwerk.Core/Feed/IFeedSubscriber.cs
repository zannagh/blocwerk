// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>
/// Receives a wall's deliveries. Called on the hub's loop thread (a circuit has no dispatcher of its own): evict, raise an
/// event and return; marshal UI work with <c>InvokeAsync</c>, never block, never touch cached entities in place.
/// </summary>
public interface IFeedSubscriber
{
    void OnFeed(FeedDelivery delivery);
}
