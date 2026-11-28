// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Social;

/// <summary>
/// <c>user.profile</c> for the owner's own devices when an Apple ID is linked, replaced or unlinked, so the profile's
/// <c>appleLinked</c> is read again. Nobody else sees whether an account has an Apple ID.
/// </summary>
internal sealed class AppleLinkFeedMapper : IFeedMapper
{
    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(AppleLink)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            return;
        }

        var userId = ((AppleLink)entry.Entity).UserId;
        context.Add(new WallChange
        {
            Kind = FeedKinds.UserProfile,
            EntityId = userId,
            Audience = FeedAudience.User,
            AudienceUserId = userId,
            ActorUserId = userId,
            SchemaVersion = FeedSchema.PayloadVersion,
        });
    }
}
