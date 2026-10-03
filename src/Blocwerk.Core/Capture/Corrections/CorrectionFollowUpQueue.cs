// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.Channels;

namespace Blocwerk.Core.Capture.Corrections;

/// <summary>
/// Captures whose model was corrected (or reverted) and whose follow-up chain has to run again on the model now live. In
/// memory like <see cref="WallCaptureQueue"/>: the app is a single instance, and a restart before the chain ran only
/// leaves the placements of the previous version (the next correction or capture re-derives them).
/// </summary>
public sealed class CorrectionFollowUpQueue
{
    private readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Queues the chain for a capture.</summary>
    /// <param name="captureId">The capture.</param>
    public void Enqueue(Guid captureId) => channel.Writer.TryWrite(captureId);

    /// <summary>The next capture.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Its id.</returns>
    public ValueTask<Guid> DequeueAsync(CancellationToken ct) => channel.Reader.ReadAsync(ct);
}
