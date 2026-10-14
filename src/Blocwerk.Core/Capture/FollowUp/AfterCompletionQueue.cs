// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.Channels;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Completed captures whose slow after-completion steps are to run (<see cref="AfterCompletionWorker"/>). In memory like
/// <see cref="WallCaptureQueue"/> (the app is a single instance); the work is marked on the capture's record
/// (<see cref="CaptureFollowUpRecord.AfterCompletion"/>), so the worker queues it again after a restart.
/// </summary>
public sealed class AfterCompletionQueue
{
    private readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Queues a capture.</summary>
    /// <param name="captureId">The capture.</param>
    public void Enqueue(Guid captureId) => channel.Writer.TryWrite(captureId);

    /// <summary>The next capture.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Its id.</returns>
    public ValueTask<Guid> DequeueAsync(CancellationToken ct) => channel.Reader.ReadAsync(ct);
}
