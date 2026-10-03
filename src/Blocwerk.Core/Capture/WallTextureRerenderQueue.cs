// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.Channels;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Captures whose wall textures are to be rendered again, for <see cref="WallTextureRerenderWorker"/>. In memory like the
/// capture queue: the row's <see cref="Entities.WallCapture.TexturesJobId"/> mark is the durable truth and the worker
/// re-enqueues marked captures on start.
/// </summary>
public sealed class WallTextureRerenderQueue
{
    private readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Guid captureId) => channel.Writer.TryWrite(captureId);

    public ValueTask<Guid> DequeueAsync(CancellationToken ct) => channel.Reader.ReadAsync(ct);
}
