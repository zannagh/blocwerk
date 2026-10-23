// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Social;

/// <summary>
/// <c>activity.added</c> for the wall's members, credited to the entry's user (the Ghost user for an anonymous kiosk tick).
/// The payload is the activity type only; the free-text details stay in the log. An entry about a boulder is placed
/// through the boulder, so a draft's entries reach members only. An entry is only ever deleted with its attempt
/// (<c>AttemptActivityEntries</c>): <c>activity.removed</c>, to the same members, so the lists showing it re-read. Updates
/// (erasure blanks the details) map to nothing.
/// </summary>
internal sealed class ActivityLogFeedMapper : IFeedMapper
{
    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(ActivityLogEntry)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        if (entry.State is not (EntityState.Added or EntityState.Deleted))
        {
            return;
        }

        var log = (ActivityLogEntry)entry.Entity;
        var added = entry.State == EntityState.Added;
        context.Add(new WallChange
        {
            WallId = log.BoulderId is null ? log.WallId : null,
            BoulderId = log.BoulderId,
            Kind = added ? FeedKinds.ActivityAdded : FeedKinds.ActivityRemoved,
            EntityId = log.Id,
            Audience = FeedAudience.Members,
            ActorUserId = log.UserId,
            DataJson = FeedJson.Serialize(new ActivityPayload(log.Type.ToString())),
            SchemaVersion = FeedSchema.PayloadVersion,
        });
    }
}
