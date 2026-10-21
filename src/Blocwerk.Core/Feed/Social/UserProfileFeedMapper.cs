// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Social;

/// <summary>
/// <c>user.profile</c>: the user's own devices on any profile or display-preference change, and, when the name or avatar
/// changed, each wall they belong to (expanded by <see cref="FeedUserWalls"/>) so open views re-read the byline. The name
/// and avatar are resolved at delivery. The wall rows go to the wall's members, not to share or kiosk viewers: a wall-wide
/// row would tell those viewers who the members are, which no page shows them. Logins, lockouts, credentials and
/// erasure (PR 1.3) map to nothing.
/// </summary>
internal sealed class UserProfileFeedMapper : IFeedMapper
{
    private static readonly IReadOnlySet<string> Byline = new HashSet<string>
    {
        nameof(User.DisplayName), nameof(User.CustomDisplayName), nameof(User.AvatarImage), nameof(User.AvatarContentType),
    };

    private static readonly IReadOnlySet<string> OwnPreferences = new HashSet<string>
    {
        nameof(User.PreferFontGrades), nameof(User.ShowToolsInNav), nameof(User.HomeWallId),
    };

    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(User)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        var user = (User)entry.Entity;
        if (entry.State != EntityState.Modified || user.IsDeleted || GhostUser.Is(user.Id))
        {
            return;
        }

        var byline = SocialRows.Changed(entry, Byline);
        if (!byline && !SocialRows.Changed(entry, OwnPreferences))
        {
            return;
        }

        context.Add(Row(user.Id, FeedAudience.User));
        if (byline)
        {
            context.Add(Row(user.Id, FeedAudience.Members));
        }
    }

    // Wall-less: the self row goes to /api/me, the members row is expanded to one per wall of the user.
    private static WallChange Row(Guid userId, FeedAudience audience) => new()
    {
        Kind = FeedKinds.UserProfile,
        EntityId = userId,
        Audience = audience,
        AudienceUserId = audience == FeedAudience.User ? userId : null,
        ActorUserId = userId,
        SchemaVersion = FeedSchema.PayloadVersion,
    };
}
