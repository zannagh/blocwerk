// <copyright file="WallRefreshQueue.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Threading.Channels;

namespace Blocwerk.Core.Refresh;

/// <summary>Refreshes waiting for the worker. In memory; the row status is the durable truth.</summary>
public sealed class WallRefreshQueue
{
    private readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Guid refreshId) => channel.Writer.TryWrite(refreshId);

    public ValueTask<Guid> DequeueAsync(CancellationToken ct) => channel.Reader.ReadAsync(ct);
}
