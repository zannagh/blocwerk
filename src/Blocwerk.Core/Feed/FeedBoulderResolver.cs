// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Turns a save's touched boulders (<see cref="FeedMapContext.TouchBoulder"/>) into one <c>boulder.changed</c> each, after
/// every entry is mapped. A boulder that wrote its own row needs nothing more. A tracked boulder gives its own wall, draft
/// flag and generation. Inside a <see cref="FeedScope"/> the row collapses to a resync anyway, so the part's wall or the
/// scope's wall is enough. Only what is left (a boulder hold saved without its boulder, outside a scope) is read from
/// the database, in one query per save. That lookup runs before EF writes anything, so a boulder deleted in the same
/// save is still there; it never fails the save: on an error the known walls get a resync instead. A part whose mapper
/// threw (<see cref="FeedMapContext.FailBoulder"/>) gets a <c>mapping-failed</c> resync of the boulder's wall, found the same way.
/// </summary>
internal static class FeedBoulderResolver
{
    /// <summary>The SQL comment on the boulder lookup, so a test or a slow-query log can tell it apart.</summary>
    public const string LookupTag = "feed-boulder-lookup";

    public static void Resolve(DbContext context, FeedMapContext map, ILogger logger)
    {
        var lookup = Prepare(map);
        if (lookup.Count == 0)
        {
            return;
        }

        try
        {
            AddFacts(map, lookup, FeedLookupSavepoint.Run(context, () => Query(context, lookup).ToList()), logger);
        }
        catch (Exception ex)
        {
            Fallback(map, lookup, ex, logger);
        }
    }

    public static async Task ResolveAsync(DbContext context, FeedMapContext map, ILogger logger, CancellationToken ct)
    {
        var lookup = Prepare(map);
        if (lookup.Count == 0)
        {
            return;
        }

        try
        {
            AddFacts(map, lookup, await FeedLookupSavepoint.RunAsync(context, () => Query(context, lookup).ToListAsync(ct), ct), logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fallback(map, lookup, ex, logger);
        }
    }

    // Writes the touches that need no lookup and returns the ones that do.
    private static List<FeedBoulderTouch> Prepare(FeedMapContext map)
    {
        if (map.TouchedBoulders.Count == 0)
        {
            return [];
        }

        var own = map.Rows.Where(FeedBoulderRows.IsBoulderRow).Select(r => r.BoulderId!.Value).ToHashSet();
        var scope = FeedScope.Active;
        var lookup = new List<FeedBoulderTouch>();
        foreach (var touch in map.TouchedBoulders.Where(t => t.MappingFailed || !own.Contains(t.BoulderId)))
        {
            if (touch.Boulder is { } boulder)
            {
                map.Add(Row(touch, boulder.WallId, boulder.Generation, boulder.IsDraft, map));
            }
            else if (scope is not null)
            {
                map.Add(FeedRows.Resync(touch.WallId ?? scope.WallId, scope.Label, map.ActorUserId, map.Now));
            }
            else
            {
                lookup.Add(touch);
            }
        }

        return lookup;
    }

    private static IQueryable<FeedBoulderFacts> Query(DbContext context, List<FeedBoulderTouch> lookup)
    {
        var ids = lookup.Select(t => t.BoulderId).ToList();
        return context.Set<Boulder>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .TagWith(LookupTag)
            .Where(b => ids.Contains(b.Id))
            .Select(b => new FeedBoulderFacts(b.Id, b.WallId, b.IsDraft, b.Generation));
    }

    // A boulder the lookup does not find is gone; its delete wrote, or writes, its own row.
    private static void AddFacts(FeedMapContext map, List<FeedBoulderTouch> lookup, List<FeedBoulderFacts> facts, ILogger logger)
    {
        var byId = facts.ToDictionary(f => f.Id);
        foreach (var touch in lookup)
        {
            if (byId.TryGetValue(touch.BoulderId, out var fact))
            {
                map.Add(Row(touch, fact.WallId, fact.Generation, fact.IsDraft, map));
            }
            else if (touch.MappingFailed)
            {
                logger.LogError("Feed mapping failed on a part of boulder {BoulderId}, which no longer exists; no feed row is written for it", touch.BoulderId);
            }
        }
    }

    // A part whose mapper threw resyncs the boulder's wall; any other touch is the boulder's one boulder.changed.
    private static WallChange Row(FeedBoulderTouch touch, Guid wallId, int generation, bool isDraft, FeedMapContext map) =>
        touch.MappingFailed
            ? FeedRows.Resync(wallId, FeedCollector.MappingFailedReason, map.ActorUserId, map.Now)
            : FeedBoulderRows.Changed(touch.BoulderId, wallId, generation, isDraft, isDraft);

    // A failed lookup resyncs every wall it can name (the part's, else the scope's) and says plainly what it could not.
    private static void Fallback(FeedMapContext map, List<FeedBoulderTouch> lookup, Exception ex, ILogger logger)
    {
        var scopeWall = FeedScope.Active?.WallId;
        var walls = lookup.Select(t => t.WallId ?? scopeWall).OfType<Guid>().Distinct().ToList();
        var lost = lookup.Where(t => (t.WallId ?? scopeWall) is null).Select(t => t.BoulderId).ToList();
        logger.LogError(ex, "Feed boulder lookup failed for {Count} boulder(s); writing a resync of {Walls} instead", lookup.Count, walls);
        if (lost.Count > 0)
        {
            logger.LogError("Feed boulder lookup failed and the wall of boulder(s) {BoulderIds} is unknown: no feed row was written for them", lost);
        }

        foreach (var wall in walls)
        {
            map.Add(FeedRows.Resync(wall, FeedCollector.MappingFailedReason, map.ActorUserId, map.Now));
        }
    }
}
