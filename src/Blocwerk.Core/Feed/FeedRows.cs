// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Feed;

/// <summary>Builders for the rows the feed writes itself, whatever the mappers do.</summary>
public static class FeedRows
{
    /// <summary>A coarse "reload this wall" row, readable by anyone who may view the wall.</summary>
    public static WallChange Resync(Guid wallId, string reason, Guid? actorUserId, DateTimeOffset now) => new()
    {
        WallId = wallId,
        Kind = FeedKinds.WallResync,
        EntityId = wallId,
        Audience = FeedAudience.Wall,
        ActorUserId = actorUserId,
        DataJson = FeedJson.Serialize(new ResyncPayload(reason)),
        SchemaVersion = FeedSchema.PayloadVersion,
        CreatedAt = now,
    };
}
