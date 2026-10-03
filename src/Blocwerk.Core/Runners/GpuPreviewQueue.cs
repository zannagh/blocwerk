// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.Channels;

namespace Blocwerk.Core.Runners;

/// <summary>
/// GPU jobs with a delivered preview to install, for <c>GpuPreviewWorker</c>. In memory like the capture queue: the job
/// row is the durable truth and the worker re-enqueues pending previews on start.
/// </summary>
public sealed class GpuPreviewQueue
{
    private readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Guid jobId) => channel.Writer.TryWrite(jobId);

    public ValueTask<Guid> DequeueAsync(CancellationToken ct) => channel.Reader.ReadAsync(ct);
}
