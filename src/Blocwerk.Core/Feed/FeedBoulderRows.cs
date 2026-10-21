// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The <c>boulder.changed</c>/<c>boulder.deleted</c> rows (design §5.4). A draft's rows go to setters only; every other
/// row goes to the wall, including the one that turns a published boulder into a draft, so viewers who saw it learn
/// that it is gone from their view (their reload no longer finds it).
/// </summary>
public static class FeedBoulderRows
{
    public static WallChange Changed(Guid boulderId, Guid wallId, int generation, bool isDraft, bool settersOnly) =>
        Row(FeedKinds.BoulderChanged, boulderId, wallId, generation, isDraft, settersOnly);

    public static WallChange Deleted(Guid boulderId, Guid wallId, int generation, bool wasDraft) =>
        Row(FeedKinds.BoulderDeleted, boulderId, wallId, generation, wasDraft, settersOnly: wasDraft);

    /// <summary>True for a row of either boulder kind.</summary>
    public static bool IsBoulderRow(WallChange row) =>
        row.BoulderId is not null && row.Kind is FeedKinds.BoulderChanged or FeedKinds.BoulderDeleted;

    private static WallChange Row(string kind, Guid boulderId, Guid wallId, int generation, bool isDraft, bool settersOnly) => new()
    {
        WallId = wallId,
        BoulderId = boulderId,
        Kind = kind,
        EntityId = boulderId,
        Audience = settersOnly ? FeedAudience.Setters : FeedAudience.Wall,
        DataJson = FeedJson.Serialize(new BoulderPayload(generation, isDraft)),
        SchemaVersion = FeedSchema.PayloadVersion,
    };
}
