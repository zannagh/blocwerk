// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Feed.Structure;

/// <summary>Builds the wall-structure family's rows: ids, kind, audience and an ids-only payload.</summary>
internal static class StructureRows
{
    public static WallChange Wall(string kind, Guid wallId, Guid entityId, FeedPayload? payload) =>
        Row(kind, wallId, entityId, FeedAudience.Wall, payload);

    public static WallChange Row(string kind, Guid wallId, Guid entityId, FeedAudience audience, FeedPayload? payload) => new()
    {
        WallId = wallId,
        Kind = kind,
        EntityId = entityId,
        Audience = audience,
        DataJson = payload is null ? null : FeedJson.Serialize(payload),
        SchemaVersion = FeedSchema.PayloadVersion,
    };
}
