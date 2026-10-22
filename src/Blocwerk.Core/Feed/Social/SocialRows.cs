// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Social;

/// <summary>
/// The row shapes of the social family. Boulder-scoped rows leave <c>WallId</c> unset: the collector fills it from the
/// boulder and narrows a draft's wall-wide rows to members (<see cref="FeedBoulderPlacement"/>).
/// </summary>
internal static class SocialRows
{
    /// <summary>A row about <paramref name="entityId"/> on a boulder, for everyone who may view the wall.</summary>
    public static WallChange OnBoulder(Guid boulderId, string kind, Guid entityId, FeedPayload? payload) => new()
    {
        BoulderId = boulderId,
        Kind = kind,
        EntityId = entityId,
        Audience = FeedAudience.Wall,
        DataJson = payload is null ? null : FeedJson.Serialize(payload),
        SchemaVersion = FeedSchema.PayloadVersion,
    };

    /// <summary>
    /// Adds the coarse <c>boulder.stats</c> of a boulder once per save, with no actor: the wall learns the counts moved,
    /// never who ticked or rated.
    /// </summary>
    public static void AddStats(FeedMapContext context, Guid boulderId)
    {
        if (!Has(context, FeedKinds.BoulderStats, boulderId, null))
        {
            context.AddWithoutActor(OnBoulder(boulderId, FeedKinds.BoulderStats, boulderId, payload: null));
        }
    }

    /// <summary>
    /// Adds a "mine" row (<c>tick.mine</c>, <c>rating.mine</c>, <c>favorite.changed</c>) once per save: the
    /// <see cref="FeedAudience.User"/> audience of <paramref name="userId"/> only, never another user or a kiosk. The
    /// Ghost user has no devices to tell.
    /// </summary>
    public static void AddMine(FeedMapContext context, string kind, Guid userId, Guid boulderId)
    {
        if (userId == Guid.Empty || GhostUser.Is(userId) || Has(context, kind, boulderId, userId))
        {
            return;
        }

        context.Add(new WallChange
        {
            BoulderId = boulderId,
            Kind = kind,
            EntityId = boulderId,
            Audience = FeedAudience.User,
            AudienceUserId = userId,
            ActorUserId = userId,
            SchemaVersion = FeedSchema.PayloadVersion,
        });
    }

    /// <summary>True when a Modified entry changed one of <paramref name="properties"/>; Added and Deleted always count.</summary>
    public static bool Changed(EntityEntry entry, IReadOnlySet<string> properties) =>
        entry.State != Microsoft.EntityFrameworkCore.EntityState.Modified
        || entry.Properties.Any(p => p.IsModified && properties.Contains(p.Metadata.Name));

    private static bool Has(FeedMapContext context, string kind, Guid boulderId, Guid? userId) =>
        context.Rows.Any(r => r.Kind == kind && r.BoulderId == boulderId && r.AudienceUserId == userId);
}
