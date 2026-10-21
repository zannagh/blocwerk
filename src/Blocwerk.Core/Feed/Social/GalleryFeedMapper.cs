// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Social;

/// <summary><c>gallery.changed</c> (an image added, or its caption or file replaced) and <c>gallery.deleted</c>, by image id.</summary>
internal sealed class GalleryFeedMapper : IFeedMapper
{
    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(WallImage)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        var image = (WallImage)entry.Entity;
        context.Add(new WallChange
        {
            WallId = image.WallId,
            Kind = entry.State == EntityState.Deleted ? FeedKinds.GalleryDeleted : FeedKinds.GalleryChanged,
            EntityId = image.Id,
            Audience = FeedAudience.Wall,
        });
    }
}
