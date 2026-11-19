// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Social;

/// <summary><c>project.changed</c> for the user's own devices only; nobody else learns what they are working on.</summary>
internal sealed class ProjectFeedMapper : IFeedMapper
{
    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(BoulderProject)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        var project = (BoulderProject)entry.Entity;
        SocialRows.AddMine(context, FeedKinds.ProjectChanged, project.UserId, project.BoulderId);
    }
}
