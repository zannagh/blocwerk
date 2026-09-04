using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Resolves recipients for domain events and enqueues Web Push sends. A singleton (it feeds the
/// shared <see cref="PushSendQueue"/>); it uses <see cref="RootDbContextFactory"/> because it lives
/// outside any user session. See the recipient-resolution methods in the partial file.
/// </summary>
public sealed partial class PushNotificationService : IPushNotificationService
{
    // RootDbContextFactory (not the scoped kiosk-stamped factory): a singleton service must not
    // capture a scoped dependency, and these are global reads/writes. Held as the interface so the
    // async create is available (matches TelemetryStatsCollector).
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly PushSendQueue queue;
    private readonly VapidOptions vapid;
    private readonly ILogger<PushNotificationService> logger;

    public PushNotificationService(
        RootDbContextFactory dbContextFactory,
        PushSendQueue queue,
        VapidOptions vapid,
        ILogger<PushNotificationService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.queue = queue;
        this.vapid = vapid;
        this.logger = logger;
    }

    public string? PublicKey => vapid.IsConfigured ? vapid.PublicKey : null;

    // The client re-saves the current device's subscription on every navigation. When nothing
    // material changed we skip the UPDATE if we already refreshed LastSeenUtc this recently, so a
    // subscribed device browsing the app does not amplify into a write per page view.
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromDays(1);

    public async Task SaveSubscriptionAsync(Guid userId, string endpoint, string p256dh, string auth, string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(p256dh) || string.IsNullOrWhiteSpace(auth))
        {
            return;
        }

        var now = DateTime.UtcNow;
        await using var db = await dbContextFactory.CreateDbContextAsync();

        var existing = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint);
        if (existing is null)
        {
            db.PushSubscriptions.Add(new PushSubscription
            {
                UserId = userId,
                Endpoint = endpoint,
                P256dh = p256dh,
                Auth = auth,
                UserAgent = userAgent,
                CreatedAtUtc = now,
                LastSeenUtc = now,
            });

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Upsert race: a concurrent save of the same NEW endpoint won the unique index first.
                // Reload the now-existing row and apply this save as an update instead of surfacing
                // the exception into the caller's circuit.
                db.ChangeTracker.Clear();
                var raced = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint);
                if (raced is not null)
                {
                    ApplyUpdate(raced, userId, p256dh, auth, userAgent, now);
                    await db.SaveChangesAsync();
                }
            }

            return;
        }

        // Write-amplification early-out: same owner, same keys, and recently refreshed => nothing to do.
        if (existing.UserId == userId
            && existing.P256dh == p256dh
            && existing.Auth == auth
            && existing.LastSeenUtc is { } seen
            && now - seen < FreshnessWindow)
        {
            return;
        }

        ApplyUpdate(existing, userId, p256dh, auth, userAgent, now);
        await db.SaveChangesAsync();
    }

    // Upsert-by-endpoint deliberately REASSIGNS ownership to the saving user. This is correct for a
    // shared installed PWA: user A logs out and user B logs in reusing the same browser push
    // endpoint, so the row must follow the current user rather than be rejected.
    private static void ApplyUpdate(
        PushSubscription row, Guid userId, string p256dh, string auth, string? userAgent, DateTime now)
    {
        row.UserId = userId;
        row.P256dh = p256dh;
        row.Auth = auth;
        row.UserAgent = userAgent;
        row.LastSeenUtc = now;
    }

    public async Task RemoveSubscriptionAsync(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return;
        }

        // Scoped by endpoint alone: this singleton lives outside any user session (RootDbContextFactory),
        // so it cannot resolve a current user, and the only caller is a device unsubscribing its own
        // endpoint. A push endpoint is high-entropy and unguessable, so it is a sufficient capability
        // key — there is no cross-user leak in deleting strictly by it.
        await using var db = await dbContextFactory.CreateDbContextAsync();
        await db.PushSubscriptions.Where(s => s.Endpoint == endpoint).ExecuteDeleteAsync();
    }

    /// <summary>
    /// Runs a notify body defensively so every call site can simply <c>await</c> a Notify* method
    /// without its own try/catch: it no-ops when VAPID is unconfigured, opens the shared read context
    /// the body works against, and never lets a failure in recipient resolution or enqueue escape —
    /// a push is best-effort and must never break or block the domain operation that raised it.
    /// </summary>
    private async Task GuardAsync(string operation, Func<BlocwerkDbContext, Task> body)
    {
        if (!vapid.IsConfigured)
        {
            return;
        }

        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync();
            await body(db);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Push notification '{Operation}' failed and was suppressed.", operation);
        }
    }

    /// <summary>
    /// The single fan-out seam: resolves the send targets for the (already actor-excluded) recipients
    /// and enqueues one send each.
    /// </summary>
    private async Task SendToUsersAsync(IEnumerable<Guid> userIds, PushPayload payload, NotificationType type)
    {
        if (!vapid.IsConfigured)
        {
            return;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync();
        var targets = await ResolveSendTargetsAsync(db, userIds, type);
        if (targets.Count == 0)
        {
            return;
        }

        var json = JsonSerializer.Serialize(payload);
        foreach (var target in targets)
        {
            queue.TryEnqueue(new PushSendJob(target.SubscriptionId, target.Endpoint, target.P256dh, target.Auth, json));
        }
    }

    /// <summary>
    /// Resolves the actual send targets for a set of candidate recipients: drops empty and duplicate
    /// ids, removes deleted users and anyone who opted out of <paramref name="type"/> (the mask is
    /// opt-OUT, so "enabled" means the bit is NOT set), then expands to their stored subscriptions —
    /// so a candidate with no subscription contributes nothing. The caller has already excluded the
    /// actor. Pure over its <paramref name="db"/> argument, which is what makes it unit-testable.
    /// </summary>
    internal static async Task<List<PushSendTarget>> ResolveSendTargetsAsync(
        BlocwerkDbContext db,
        IEnumerable<Guid> userIds,
        NotificationType type)
    {
        var ids = userIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var typeValue = (int)type;
        var enabledUserIds = await db.Users
            .Where(u => ids.Contains(u.Id) && u.DeletedAt == null && ((int)u.DisabledNotifications & typeValue) == 0)
            .Select(u => u.Id)
            .ToListAsync();

        if (enabledUserIds.Count == 0)
        {
            return [];
        }

        return await db.PushSubscriptions
            .Where(s => enabledUserIds.Contains(s.UserId))
            .Select(s => new PushSendTarget(s.Id, s.Endpoint, s.P256dh, s.Auth))
            .ToListAsync();
    }
}
