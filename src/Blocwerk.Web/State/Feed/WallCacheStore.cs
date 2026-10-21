// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Feed;

/// <summary>
/// The cached reads of one circuit and the scoped evictions the feed applies to them (design §5.5). What each entry holds
/// decides what evicts it:
/// <list type="bullet">
/// <item><description>the wall aggregate: the wall row, its members, its live holds and its active boulders (with their
/// creator and hold links);</description></item>
/// <item><description>a boulder: the boulder, its hold links with the holds, its setters and creator, and the slim wall row
/// (generation, share token);</description></item>
/// <item><description>a hold set: the holds of one wall generation.</description></item>
/// </list>
/// </summary>
public sealed class WallCacheStore
{
    public FeedTaggedCache<Guid, Wall?> Walls { get; } = new();

    public FeedTaggedCache<Guid, Boulder?> Boulders { get; } = new();

    public FeedTaggedCache<(Guid Wall, int Generation), List<Hold>> HoldSets { get; } = new();

    /// <summary>Drops everything cached of the wall; true when anything was cached.</summary>
    public bool EvictWall(Guid wallId)
    {
        if (wallId == Guid.Empty)
        {
            return false;
        }

        var any = Walls.Evict(wallId);
        any |= HoldSets.EvictWhere((key, _) => key.Wall == wallId).Count > 0;
        any |= Boulders.EvictWhere((_, boulder) => boulder?.WallId == wallId).Count > 0;
        return any;
    }

    /// <summary>The wall aggregate, unless it already reflects the event.</summary>
    public bool EvictAggregate(Guid wallId, FeedCursor cursor) => Walls.EvictFor(wallId, cursor);

    /// <summary>One boulder, unless it already reflects the event.</summary>
    public bool EvictBoulder(Guid boulderId, FeedCursor cursor) => Boulders.EvictFor(boulderId, cursor);

    /// <summary>One generation's hold set; nothing when that generation is not cached.</summary>
    public bool EvictHoldSet(Guid wallId, int generation, FeedCursor cursor) => HoldSets.EvictFor((wallId, generation), cursor);

    /// <summary>Every cached boulder of the wall that matches and predates the event; returns their ids.</summary>
    public List<Guid> EvictBoulders(Guid wallId, FeedCursor cursor, Func<Boulder, bool> match) =>
        Boulders.EvictWhere((_, boulder) => boulder is not null && boulder.WallId == wallId && match(boulder)
            && PredatesOrUnknown(boulder.Id, cursor));

    private bool PredatesOrUnknown(Guid boulderId, FeedCursor cursor) =>
        !Boulders.TryPeek(boulderId, out var load) || load.Predates(cursor);
}
