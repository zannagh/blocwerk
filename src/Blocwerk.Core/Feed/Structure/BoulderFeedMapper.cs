// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Structure;

/// <summary>
/// <see cref="Boulder"/>: <c>boulder.changed</c> or <c>boulder.deleted</c> with <c>{ generation, isDraft }</c>. A draft
/// stays with the setters while it is one (added as a draft, edited as a draft, deleted as a draft). Publishing it, and
/// taking a published boulder back to a draft, go to the whole wall (<see cref="FeedBoulderRows"/>).
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
        context.Add(FeedBoulderRows.Changed(boulder.Id, boulder.WallId, generation, isDraft, settersOnly: isDraft && wasDraft));
    }
}
