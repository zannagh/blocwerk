// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Social;

/// <summary><c>favorite.changed</c> for the user's own devices only; nobody else learns what they starred.</summary>
internal sealed class FavoriteFeedMapper : IFeedMapper
{
    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(BoulderFavorite)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        var favorite = (BoulderFavorite)entry.Entity;
        SocialRows.AddMine(context, FeedKinds.FavoriteChanged, favorite.UserId, favorite.BoulderId);
    }
}
