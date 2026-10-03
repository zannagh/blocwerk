using System.Threading.Channels;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The in-process queue of captures waiting for the worker. In memory on purpose: the app runs as a
/// SINGLE instance, and the database row is the durable truth — on start the worker re-enqueues every
/// capture a previous process left queued or in flight, so nothing is lost with this channel.
/// </summary>
public sealed class WallCaptureQueue
{
    private readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Guid captureId) => channel.Writer.TryWrite(captureId);

    public ValueTask<Guid> DequeueAsync(CancellationToken ct) => channel.Reader.ReadAsync(ct);
}
