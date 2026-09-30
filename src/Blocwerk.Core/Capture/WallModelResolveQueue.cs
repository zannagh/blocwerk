// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.Channels;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Captures whose 3D model is to be solved again, for <see cref="WallModelResolveWorker"/>. In memory like the capture
/// queue: the row's <see cref="Entities.WallCapture.SolveJobId"/> mark is the durable truth and the worker re-enqueues
/// marked captures on start.
/// </summary>
public sealed class WallModelResolveQueue
{
    private readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Guid captureId) => channel.Writer.TryWrite(captureId);

    public ValueTask<Guid> DequeueAsync(CancellationToken ct) => channel.Reader.ReadAsync(ct);
}
