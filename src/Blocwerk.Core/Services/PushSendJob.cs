namespace Blocwerk.Core.Services;

/// <summary>
/// One queued outbound Web Push send: the target subscription plus the already-serialized JSON
/// payload. Carried through the <see cref="PushSendQueue"/> so the actual HTTP send happens on the
/// background drain, never on the request/circuit thread that resolved the recipients.
/// </summary>
public sealed record PushSendJob(
    Guid SubscriptionId,
    string Endpoint,
    string P256dh,
    string Auth,
    string PayloadJson);
