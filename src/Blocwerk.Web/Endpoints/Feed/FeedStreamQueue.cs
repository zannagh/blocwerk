// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.Channels;
using Blocwerk.Core.Feed;

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>
/// The hub's subscriber for one stream. <see cref="OnFeed"/> runs on the hub's loop and must never block, so deliveries go
/// into a bounded channel; when the client reads too slowly to drain it, the queue closes instead of waiting
/// (<see cref="Overflowed"/>), and the stream ends with <c>overflow</c>. The client reconnects and catches up by pages.
/// </summary>
internal sealed class FeedStreamQueue : IFeedSubscriber
{
    private readonly Channel<FeedDelivery> channel;
    private volatile bool overflowed;

    public FeedStreamQueue(int capacity)
    {
        channel = Channel.CreateBounded<FeedDelivery>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public ChannelReader<FeedDelivery> Reader => channel.Reader;

    /// <summary>Gets a value indicating whether a delivery was dropped because the queue was full.</summary>
    public bool Overflowed => overflowed;

    public void OnFeed(FeedDelivery delivery)
    {
        if (!channel.Writer.TryWrite(delivery))
        {
            overflowed = true;
            channel.Writer.TryComplete();
        }
    }
}
