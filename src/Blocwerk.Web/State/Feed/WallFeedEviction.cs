// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Feed;

/// <summary>
/// Maps one feed event to the scoped eviction it needs (design §5.5). An event is a hint keyed by id: it never patches a
/// cached value, it removes the entries that may be stale, and the next read reloads them. An entry whose
/// <c>CursorAtLoad</c> is at or after the event already reflects it and stays.
/// <list type="table">
/// <item><term><c>wall.resync</c>, segment, panel, link, volume, <c>access.revoked</c>, unreadable cursor, unknown
/// <c>sv</c></term><description>everything of the wall.</description></item>
/// <item><term><c>wall.updated</c></term><description>the aggregate and the wall's cached boulders (they carry its slim
/// row).</description></item>
/// <item><term><c>member.changed</c></term><description>the aggregate.</description></item>
/// <item><term><c>hold.changed</c>, <c>hold.deleted</c></term><description>that generation's hold set (if cached), the
/// aggregate (it carries the live holds) and the cached boulders that link the hold.</description></item>
/// <item><term><c>boulder.changed</c>, <c>boulder.deleted</c></term><description>that boulder and the aggregate (its
/// boulder list).</description></item>
/// <item><term><c>user.profile</c></term><description>the aggregate and the boulders the user created or set.</description></item>
/// <item><term>everything else</term><description>nothing: stats, comments, videos, gallery, 3D, activity and the user's
/// own rows live in no cache here (PR 1.5's leaf lists), and <c>wall.manifest</c> is read only by the server's manifest
/// cache (the aggregate holds no manifest data).</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <b>Rule:</b> every entity type a cached read hands out (<see cref="CachedShapes"/>) must name the kinds that evict
/// it, and a change to any of its columns a page shows must write one of them. A cached read that starts loading another
/// entity type fails <c>WallCacheShapeGuardTests</c> until it is listed here, with its kinds.
/// </remarks>
public static class WallFeedEviction
{
    /// <summary>
    /// Gets the entity types the cached reads hold (the wall aggregate, a boulder, a hold set), each with the kinds that
    /// evict the entries holding it. Owned types travel with their owner.
    /// </summary>
    public static IReadOnlyDictionary<Type, IReadOnlySet<string>> CachedShapes { get; } = new Dictionary<Type, IReadOnlySet<string>>
    {
        [typeof(Core.Entities.Wall)] = Kinds(FeedKinds.WallUpdated, FeedKinds.WallResync),
        [typeof(Core.Entities.WallMember)] = Kinds(FeedKinds.MemberChanged),
        [typeof(Core.Entities.Hold)] = Kinds(FeedKinds.HoldChanged, FeedKinds.HoldDeleted),
        [typeof(Core.Entities.Boulder)] = Kinds(FeedKinds.BoulderChanged, FeedKinds.BoulderDeleted),
        [typeof(Core.Entities.BoulderHold)] = Kinds(FeedKinds.BoulderChanged),
        [typeof(Core.Entities.BoulderSetter)] = Kinds(FeedKinds.BoulderChanged),
        [typeof(Core.Entities.User)] = Kinds(FeedKinds.UserProfile),
    };

    public static WallFeedEvicted Apply(WallCacheStore store, Guid wallId, FeedEnvelope e)
    {
        if (!FeedKinds.IsKnown(e.Kind))
        {
            return WallFeedEvicted.None;
        }

        if (!FeedCursor.TryParse(e.Cursor, out var cursor) || !IsSupported(e.Data))
        {
            return WallFeedEvicted.Whole(store.EvictWall(wallId));
        }

        return e.Kind switch
        {
            FeedKinds.WallResync or FeedKinds.SegmentChanged or FeedKinds.PanelChanged or FeedKinds.LinkChanged
                or FeedKinds.VolumeChanged or FeedKinds.AccessRevoked => WallFeedEvicted.Whole(store.EvictWall(wallId)),
            FeedKinds.WallUpdated => Aggregate(store, wallId, cursor, store.EvictBoulders(wallId, cursor, _ => true)),
            FeedKinds.MemberChanged => Aggregate(store, wallId, cursor, WallFeedEvicted.None),
            FeedKinds.HoldChanged or FeedKinds.HoldDeleted => Hold(store, wallId, cursor, e),
            FeedKinds.BoulderChanged or FeedKinds.BoulderDeleted => Boulder(store, wallId, cursor, e.EntityId),
            FeedKinds.UserProfile => Aggregate(store, wallId, cursor, store.EvictBoulders(
                wallId, cursor, b => b.CreatedByUserId == e.EntityId || b.Setters.Any(s => s.UserId == e.EntityId))),
            _ => WallFeedEvicted.None,
        };
    }

    private static IReadOnlySet<string> Kinds(params string[] kinds) => new HashSet<string>(kinds, StringComparer.Ordinal);

    private static WallFeedEvicted Aggregate(WallCacheStore store, Guid wallId, FeedCursor cursor, WallFeedEvicted boulders) =>
        boulders with { Wall = store.EvictAggregate(wallId, cursor) };

    private static WallFeedEvicted Hold(WallCacheStore store, Guid wallId, FeedCursor cursor, FeedEnvelope e)
    {
        var sets = Generation(e.Data) is { } generation
            ? store.EvictHoldSet(wallId, generation, cursor)
            : store.EvictHoldSets(wallId);
        var boulders = store.EvictBoulders(wallId, cursor, b => b.BoulderHolds.Any(bh => bh.HoldId == e.EntityId));
        var aggregate = store.EvictAggregate(wallId, cursor);
        return boulders with { Wall = aggregate || sets };
    }

    private static WallFeedEvicted Boulder(WallCacheStore store, Guid wallId, FeedCursor cursor, Guid? boulderId)
    {
        var boulder = boulderId is { } id && store.EvictBoulder(id, cursor);
        return new WallFeedEvicted(store.EvictAggregate(wallId, cursor), boulder ? [boulderId!.Value] : []);
    }

    private static int? Generation(JsonElement? data) =>
        data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("generation", out var g) && g.TryGetInt32(out var n)
            ? n
            : null;

    // A payload of a newer schema than this build knows means "resync that wall" (design §5.4).
    private static bool IsSupported(JsonElement? data) =>
        data is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty("sv", out var sv)
        || (sv.TryGetInt32(out var version) && FeedSchema.IsSupported(version));
}
