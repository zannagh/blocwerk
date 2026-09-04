namespace Blocwerk.Core.Services;

/// <summary>
/// One resolved recipient subscription — the output of
/// <see cref="PushNotificationService.ResolveSendTargetsAsync"/> before the payload is attached and
/// the send is enqueued as a <see cref="PushSendJob"/>. Kept separate from the job so recipient
/// resolution can be unit-tested without serializing a payload or touching the send queue.
/// </summary>
internal sealed record PushSendTarget(
    Guid SubscriptionId,
    string Endpoint,
    string P256dh,
    string Auth);
