// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Structure;

/// <summary>
/// <see cref="Hold"/>: <c>hold.changed</c> or <c>hold.deleted</c> with <c>{ generation, panelId }</c>, so a consumer
/// evicts only that generation's hold set (design §5.5). A hold restamped onto another generation or panel writes a
/// second <c>hold.changed</c> with the old pair, so the set it left reloads too.
/// </summary>
internal sealed class HoldFeedMapper : IFeedMapper
{
    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(Hold)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        var hold = (Hold)entry.Entity;
        var deleted = entry.State == EntityState.Deleted;
        var generation = FeedEntryValues.Value<int>(entry, nameof(Hold.Generation));
        var panelId = FeedEntryValues.Value<Guid?>(entry, nameof(Hold.WallPanelId));
        var kind = deleted ? FeedKinds.HoldDeleted : FeedKinds.HoldChanged;
        context.Add(StructureRows.Wall(kind, hold.WallId, hold.Id, new HoldPayload(generation, panelId)));
        if (entry.State != EntityState.Modified)
        {
            return;
        }

        var oldGeneration = FeedEntryValues.Original<int>(entry, nameof(Hold.Generation));
        var oldPanelId = FeedEntryValues.Original<Guid?>(entry, nameof(Hold.WallPanelId));
        if (oldGeneration != generation || oldPanelId != panelId)
        {
            context.Add(StructureRows.Wall(FeedKinds.HoldChanged, hold.WallId, hold.Id, new HoldPayload(oldGeneration, oldPanelId)));
        }
    }
}
