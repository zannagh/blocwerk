// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.Channels;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The single-instance <see cref="IFeedSignal"/>: a bounded channel that drops the oldest wake when full. A dropped wake is
/// harmless, the hub's 30 s safety poll delivers its rows.
/// </summary>
public sealed class InProcessFeedSignal : IFeedSignal
{
    /// <summary>The most wakes waiting for the hub.</summary>
    public const int Capacity = 10_000;

    private readonly Channel<FeedWake> channel = Channel.CreateBounded<FeedWake>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });

    public ChannelReader<FeedWake> Wakes => channel.Reader;

    public void Wake(FeedWake wake) => channel.Writer.TryWrite(wake);
}
