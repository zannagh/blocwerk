// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>Collects a hub's deliveries and lets a test wait for them.</summary>
internal sealed class RecordingFeedSubscriber : IFeedSubscriber
{
    private readonly List<FeedDelivery> deliveries = [];

    public IReadOnlyList<FeedDelivery> Deliveries
    {
        get
        {
            lock (deliveries)
            {
                return deliveries.ToList();
            }
        }
    }

    public IReadOnlyList<FeedEnvelope> Events => Deliveries.SelectMany(d => d.Events).ToList();

    public void OnFeed(FeedDelivery delivery)
    {
        lock (deliveries)
        {
            deliveries.Add(delivery);
        }
    }

    /// <summary>Waits until at least <paramref name="count"/> events arrived, or <paramref name="seconds"/> passed.</summary>
    public async Task<IReadOnlyList<FeedEnvelope>> WaitForEventsAsync(int count, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (Events.Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        return Events;
    }
}

/// <summary>A subscriber with a bug.</summary>
internal sealed class ThrowingFeedSubscriber : IFeedSubscriber
{
    public void OnFeed(FeedDelivery delivery) => throw new InvalidOperationException("subscriber bug");
}
