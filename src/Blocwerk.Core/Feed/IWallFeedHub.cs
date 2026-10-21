// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>
/// The server-side fan-out of the feed (design §5.2): one background loop that reads each subscribed wall below the
/// horizon, hydrates the rows and hands them to the wall's subscribers. Nothing subscribes in PR 1.2a; circuits move onto
/// it in 1.4.
/// </summary>
public interface IWallFeedHub
{
    /// <summary>
    /// Subscribes to <paramref name="wallId"/> for <paramref name="viewer"/>. The caller has checked that the viewer may read
    /// the wall. Deliveries start after <see cref="FeedSubscription.Start"/>; a caller that loaded its state at an older
    /// <c>CursorAtLoad</c> reads the gap from <see cref="IWallFeed"/> or reloads.
    /// <para>
    /// <see cref="IFeedSubscriber.OnFeed"/> runs synchronously on the hub's loop under the wall's gate: it must not block,
    /// must hand UI work off (<c>InvokeAsync</c>, not awaited), and must tolerate one more call after the subscription is
    /// disposed. Cancelling <paramref name="cancellationToken"/> leaves nothing subscribed and nothing tracked.
    /// </para>
    /// </summary>
    Task<FeedSubscription> SubscribeAsync(Guid wallId, FeedViewer viewer, IFeedSubscriber subscriber, CancellationToken cancellationToken = default);
}
