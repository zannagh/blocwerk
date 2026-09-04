using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// Saves/removes browser push subscriptions and sends notifications for domain events. Recipient
/// resolution runs inline (a fast DB query); the actual Web Push HTTP sends are drained on a
/// background queue. Every notify method takes the acting user so the actor is never notified about
/// their own action, and honours each recipient's opt-out mask. When VAPID is unconfigured the whole
/// feature is a safe no-op.
/// </summary>
public interface IPushNotificationService
{
    /// <summary>The VAPID public key handed to the client as the applicationServerKey, or null when unconfigured.</summary>
    string? PublicKey { get; }

    /// <summary>Upserts a subscription by its endpoint, refreshing keys/user-agent and LastSeenUtc.</summary>
    Task SaveSubscriptionAsync(Guid userId, string endpoint, string p256dh, string auth, string? userAgent);

    /// <summary>Removes the subscription with the given endpoint, if any.</summary>
    Task RemoveSubscriptionAsync(string endpoint);

    /// <summary>Notifies the wall's members (except the actor) that a session started.</summary>
    Task NotifySessionStartedAsync(Guid wallId, Guid actorId);

    /// <summary>Notifies the wall's members (except the actor) that a boulder was published.</summary>
    Task NotifyBoulderAddedAsync(Guid wallId, Guid boulderId, Guid actorId);

    /// <summary>Notifies the boulder's setters + creator (except the actor) of a new comment.</summary>
    Task NotifyCommentAsync(Guid boulderId, Guid actorId);

    /// <summary>Notifies the boulder's setters + creator (except the actor) of a send/flash.</summary>
    Task NotifyAscentAsync(Guid boulderId, Guid actorId, bool isFlash);

    /// <summary>Notifies the boulder's setters + creator (except the actor) of a new beta video.</summary>
    Task NotifyBetaAsync(Guid boulderId, Guid actorId);

    /// <summary>Notifies the wall's members (except the actor) that someone joined.</summary>
    Task NotifyMemberJoinedAsync(Guid wallId, Guid actorId);

    /// <summary>
    /// Broadcasts an "app back online" notice to every subscribed user who has not opted out of
    /// <see cref="NotificationType.AppOnline"/>. For use ONLY after a real downtime window.
    /// </summary>
    Task NotifyAppOnlineAsync();

    /// <summary>
    /// Notifies site administrators only (subscribed, not opted out of
    /// <see cref="NotificationType.AppOnline"/>) that a new version was deployed. Gated under the same
    /// AppOnline flag as <see cref="NotifyAppOnlineAsync"/>; sent on every deploy, including routine ones.
    /// </summary>
    Task NotifyDeploymentAsync();
}
