using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Recipient resolution for each domain event. Every method runs through <c>GuardAsync</c>, which
/// no-ops when VAPID is unconfigured, opens the read context and swallows any failure, then fetches
/// the human-readable names and the recipient ids (always excluding the actor), builds a
/// <see cref="PushPayload"/> with a deep-link url and a collapsing tag, and hands off to the shared
/// fan-out.
/// </summary>
public sealed partial class PushNotificationService
{
    private const string FallbackName = "Someone";

    public Task NotifySessionStartedAsync(Guid wallId, Guid actorId) =>
        GuardAsync(nameof(NotifySessionStartedAsync), async db =>
        {
            var wallName = await WallNameAsync(db, wallId);
            var recipients = await WallMemberIdsAsync(db, wallId, actorId);
            if (recipients.Count == 0)
            {
                return;
            }

            var actor = await ActorNameAsync(db, actorId);
            var payload = new PushPayload(
                Title: wallName,
                Body: $"{actor} started a session.",
                Url: $"/walls/{wallId}",
                Tag: $"session-{wallId}");

            await SendToUsersAsync(recipients, payload, NotificationType.SessionStarted);
        });

    public Task NotifyBoulderAddedAsync(Guid wallId, Guid boulderId, Guid actorId) =>
        GuardAsync(nameof(NotifyBoulderAddedAsync), async db =>
        {
            var wallName = await WallNameAsync(db, wallId);
            var boulder = await db.Boulders
                .Where(b => b.Id == boulderId)
                .Select(b => new { b.Name, b.Grade })
                .FirstOrDefaultAsync();
            if (boulder is null)
            {
                return;
            }

            var recipients = await WallMemberIdsAsync(db, wallId, actorId);
            if (recipients.Count == 0)
            {
                return;
            }

            var name = string.IsNullOrWhiteSpace(boulder.Grade) ? boulder.Name : $"{boulder.Name} ({boulder.Grade})";
            var payload = new PushPayload(
                Title: $"New boulder on {wallName}",
                Body: name,
                Url: $"/walls/{wallId}/boulders/{boulderId}",
                Tag: $"boulder-{boulderId}");

            await SendToUsersAsync(recipients, payload, NotificationType.BoulderAdded);
        });

    public Task NotifyCommentAsync(Guid boulderId, Guid actorId) =>
        NotifyBoulderTargetedAsync(
            boulderId,
            actorId,
            NotificationType.CommentOnYourBoulder,
            (actor, boulder) => $"{actor} commented on {boulder}.",
            "comment");

    public Task NotifyAscentAsync(Guid boulderId, Guid actorId, bool isFlash)
    {
        var verb = isFlash ? "flashed" : "sent";
        return NotifyBoulderTargetedAsync(
            boulderId,
            actorId,
            NotificationType.SendOnYourBoulder,
            (actor, boulder) => $"{actor} {verb} {boulder}.",
            "ascent");
    }

    public Task NotifyBetaAsync(Guid boulderId, Guid actorId) =>
        NotifyBoulderTargetedAsync(
            boulderId,
            actorId,
            NotificationType.BetaOnYourBoulder,
            (actor, boulder) => $"{actor} added a beta video to {boulder}.",
            "beta");

    public Task NotifyMemberJoinedAsync(Guid wallId, Guid actorId) =>
        GuardAsync(nameof(NotifyMemberJoinedAsync), async db =>
        {
            var wallName = await WallNameAsync(db, wallId);
            var recipients = await WallMemberIdsAsync(db, wallId, actorId);
            if (recipients.Count == 0)
            {
                return;
            }

            var actor = await ActorNameAsync(db, actorId);
            var payload = new PushPayload(
                Title: wallName,
                Body: $"{actor} joined the wall.",
                Url: $"/walls/{wallId}",
                Tag: $"member-{wallId}");

            await SendToUsersAsync(recipients, payload, NotificationType.MemberJoined);
        });

    public Task NotifyAppOnlineAsync() =>
        GuardAsync(nameof(NotifyAppOnlineAsync), async db =>
        {
            var recipients = await SubscribedUserIdsAsync(db, adminsOnly: false);
            if (recipients.Count == 0)
            {
                return;
            }

            var payload = new PushPayload(
                Title: "Blocwerk is back online",
                Body: "The app is back up after an update.",
                Url: "/",
                Tag: "app-online");

            await SendToUsersAsync(recipients, payload, NotificationType.AppOnline);
        });

    public Task NotifyDeploymentAsync() =>
        GuardAsync(nameof(NotifyDeploymentAsync), async db =>
        {
            var recipients = await SubscribedUserIdsAsync(db, adminsOnly: true);
            if (recipients.Count == 0)
            {
                return;
            }

            var payload = new PushPayload(
                Title: "Deployment complete",
                Body: "A new version of Blocwerk is live.",
                Url: "/",
                Tag: "deployment");

            await SendToUsersAsync(recipients, payload, NotificationType.AppOnline);
        });

    private Task NotifyBoulderTargetedAsync(
        Guid boulderId,
        Guid actorId,
        NotificationType type,
        Func<string, string, string> bodyBuilder,
        string tagPrefix) =>
        GuardAsync(tagPrefix, async db =>
        {
            var boulder = await db.Boulders
                .Where(b => b.Id == boulderId)
                .Select(b => new { b.Name, b.WallId })
                .FirstOrDefaultAsync();
            if (boulder is null)
            {
                return;
            }

            var recipients = await BoulderTargetRecipientsAsync(db, boulderId, actorId);
            if (recipients.Count == 0)
            {
                return;
            }

            var actor = await ActorNameAsync(db, actorId);
            var payload = new PushPayload(
                Title: boulder.Name,
                Body: bodyBuilder(actor, boulder.Name),
                Url: $"/walls/{boulder.WallId}/boulders/{boulderId}",
                Tag: $"{tagPrefix}-{boulderId}");

            await SendToUsersAsync(recipients, payload, type);
        });

    private static async Task<string> WallNameAsync(Data.BlocwerkDbContext db, Guid wallId)
    {
        var name = await db.Walls
            .IgnoreQueryFilters()
            .Where(w => w.Id == wallId)
            .Select(w => w.Name)
            .FirstOrDefaultAsync();
        return string.IsNullOrWhiteSpace(name) ? "your wall" : name;
    }

    /// <summary>The wall's members, excluding the actor. Internal so the actor-exclusion is unit-testable.</summary>
    internal static async Task<List<Guid>> WallMemberIdsAsync(Data.BlocwerkDbContext db, Guid wallId, Guid actorId)
    {
        return await db.WallMembers
            .Where(m => m.WallId == wallId && m.UserId != actorId)
            .Select(m => m.UserId)
            .ToListAsync();
    }

    /// <summary>
    /// The recipients of a setter-targeted event (comment/ascent/beta): the boulder's setters plus
    /// its creator, excluding the actor — never the whole wall. Empty when the boulder is gone.
    /// Internal so the setter+creator resolution is unit-testable.
    /// </summary>
    internal static async Task<List<Guid>> BoulderTargetRecipientsAsync(Data.BlocwerkDbContext db, Guid boulderId, Guid actorId)
    {
        var creatorId = await db.Boulders
            .Where(b => b.Id == boulderId)
            .Select(b => (Guid?)b.CreatedByUserId)
            .FirstOrDefaultAsync();
        if (creatorId is null)
        {
            return [];
        }

        var setterIds = await db.BoulderSetters
            .Where(s => s.BoulderId == boulderId)
            .Select(s => s.UserId)
            .ToListAsync();

        return setterIds
            .Append(creatorId.Value)
            .Where(id => id != actorId)
            .Distinct()
            .ToList();
    }

    private static async Task<List<Guid>> SubscribedUserIdsAsync(Data.BlocwerkDbContext db, bool adminsOnly)
    {
        var users = db.Users.Where(u => u.DeletedAt == null && u.PushSubscriptions.Any());
        if (adminsOnly)
        {
            users = users.Where(u => u.Role == IdentityRole.Admin);
        }

        return await users.Select(u => u.Id).ToListAsync();
    }

    private static async Task<string> ActorNameAsync(Data.BlocwerkDbContext db, Guid actorId)
    {
        var actor = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.Id == actorId)
            .Select(u => new { u.CustomDisplayName, u.DisplayName })
            .FirstOrDefaultAsync();
        if (actor is null)
        {
            return FallbackName;
        }

        var name = string.IsNullOrWhiteSpace(actor.CustomDisplayName) ? actor.DisplayName : actor.CustomDisplayName;
        return string.IsNullOrWhiteSpace(name) ? FallbackName : name;
    }
}
