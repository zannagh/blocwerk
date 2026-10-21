// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Structure;

/// <summary>
/// <see cref="Boulder"/>: <c>boulder.changed</c> or <c>boulder.deleted</c> with <c>{ generation, isDraft }</c>. A draft
/// stays with the wall's members (never share or kiosk viewers) while it is one (added as a draft, edited as a draft, deleted as a draft). Publishing it, and
/// taking a published boulder back to a draft, go to the whole wall (<see cref="FeedBoulderRows"/>). A boulder restamped onto
/// another generation writes a second row with the old one, as a hold does, so that generation's views reload too.
/// </summary>
internal sealed class BoulderFeedMapper : IFeedMapper
{
    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(Boulder)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        var boulder = (Boulder)entry.Entity;
        var generation = FeedEntryValues.Value<int>(entry, nameof(Boulder.Generation));
        var wasDraft = FeedEntryValues.Original<bool>(entry, nameof(Boulder.IsDraft));
        if (entry.State == EntityState.Deleted)
        {
            context.Add(FeedBoulderRows.Deleted(boulder.Id, boulder.WallId, generation, wasDraft));
            return;
        }

        var isDraft = boulder.IsDraft;
        context.Add(FeedBoulderRows.Changed(boulder.Id, boulder.WallId, generation, isDraft, membersOnly: isDraft && wasDraft));
        var oldGeneration = FeedEntryValues.Original<int>(entry, nameof(Boulder.Generation));
        if (entry.State == EntityState.Modified && oldGeneration != generation)
        {
            context.Add(FeedBoulderRows.Changed(boulder.Id, boulder.WallId, oldGeneration, wasDraft, membersOnly: isDraft && wasDraft));
        }
    }
}
