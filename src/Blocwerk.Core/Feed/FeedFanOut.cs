// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Hands one read's rows to a wall's subscribers, each narrowed to the audiences its viewer belongs to. Envelopes are built
/// once per visibility rule, not per subscriber. A subscriber that throws is logged and skipped; the others still get
/// their batch.
/// </summary>
internal static class FeedFanOut
{
    public static void Deliver(
        FeedHubWall wall, IReadOnlyList<WallChange> rows, FeedHydrated hydrated, FeedCursor next, ILogger logger)
    {
        var built = new Dictionary<AuthorVisibility, FeedEnvelope[]>();
        foreach (var subscriber in wall.Subscribers)
        {
            if (!built.TryGetValue(subscriber.Visibility, out var envelopes))
            {
                envelopes = rows.Select(r => FeedEnvelopes.Build(r, next.Epoch, hydrated, subscriber.Visibility)).ToArray();
                built[subscriber.Visibility] = envelopes;
            }

            var visible = new List<FeedEnvelope>();
            for (var i = 0; i < rows.Count; i++)
            {
                if (subscriber.Viewer.Allows(rows[i]))
                {
                    visible.Add(envelopes[i]);
                }
            }

            if (visible.Count > 0)
            {
                Send(subscriber, new FeedDelivery(wall.WallId, visible, next, Resync: false), logger);
            }
        }
    }

    public static void DeliverResync(FeedHubWall wall, FeedCursor next, ILogger logger)
    {
        foreach (var subscriber in wall.Subscribers)
        {
            Send(subscriber, new FeedDelivery(wall.WallId, [], next, Resync: true), logger);
        }
    }

    private static void Send(FeedHubSubscriber subscriber, FeedDelivery delivery, ILogger logger)
    {
        try
        {
            subscriber.Target.OnFeed(delivery);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "A feed subscriber of wall {WallId} threw; it misses this batch", delivery.WallId);
        }
    }
}
