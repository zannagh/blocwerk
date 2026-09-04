using System.Threading.Channels;

namespace Blocwerk.Core.Services;

/// <summary>
/// A bounded, in-process queue of pending Web Push sends. The producer is
/// <see cref="PushNotificationService"/> (resolves recipients inline and enqueues), the single
/// consumer is <see cref="PushSenderBackgroundService"/> (drains and performs the HTTP sends). A
/// singleton, shared by both. When full the oldest queued send is dropped rather than blocking the
/// request thread — a push is best-effort and a lost one is acceptable under overload.
/// </summary>
public sealed class PushSendQueue
{
    private readonly Channel<PushSendJob> channel = Channel.CreateBounded<PushSendJob>(
        new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Enqueues a send. Returns false only if the channel has been completed.</summary>
    public bool TryEnqueue(PushSendJob job)
    {
        return channel.Writer.TryWrite(job);
    }

    /// <summary>Drains queued sends until the channel completes or the token is cancelled.</summary>
    public IAsyncEnumerable<PushSendJob> ReadAllAsync(CancellationToken cancellationToken)
    {
        return channel.Reader.ReadAllAsync(cancellationToken);
    }
}
