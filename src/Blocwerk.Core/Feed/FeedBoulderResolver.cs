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
/// The same lookup places the rows a mapper wrote on a boulder without its wall (<see cref="FeedBoulderPlacement"/>).
/// </summary>
internal static class FeedBoulderResolver
{
    /// <summary>The SQL comment on the boulder lookup, so a test or a slow-query log can tell it apart.</summary>
    public const string LookupTag = "feed-boulder-lookup";

    public static void Resolve(DbContext context, FeedMapContext map, ILogger logger)
    {
        var lookup = Prepare(map);
        var plan = FeedBoulderPlacement.Plan(context, map);
        var ids = Ids(lookup, plan);
        if (ids.Count == 0)
        {
            FeedBoulderPlacement.Apply(map, plan, []);
            return;
        }

        try
        {
            Finish(map, lookup, plan, FeedLookupSavepoint.Run(context, () => Query(context, ids).ToList()), logger);
        }
        catch (Exception ex)
        {
            Fail(map, lookup, plan, ex, logger);
        }
    }

    public static async Task ResolveAsync(DbContext context, FeedMapContext map, ILogger logger, CancellationToken ct)
    {
        var lookup = Prepare(map);
        var plan = FeedBoulderPlacement.Plan(context, map);
        var ids = Ids(lookup, plan);
        if (ids.Count == 0)
        {
            FeedBoulderPlacement.Apply(map, plan, []);
            return;
        }

        try
        {
            Finish(map, lookup, plan, await FeedLookupSavepoint.RunAsync(context, () => Query(context, ids).ToListAsync(ct), ct), logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(map, lookup, plan, ex, logger);
        }
    }

    // One lookup per save, for the touched boulders and the unplaced rows' boulders alike.
    private static List<Guid> Ids(List<FeedBoulderTouch> lookup, FeedPlacementPlan plan) =>
        lookup.Select(t => t.BoulderId).Concat(plan.Missing).Distinct().ToList();

    private static void Finish(
        FeedMapContext map, List<FeedBoulderTouch> lookup, FeedPlacementPlan plan, List<FeedBoulderFacts> facts, ILogger logger)
    {
        AddFacts(map, lookup, facts, logger);
        FeedBoulderPlacement.Apply(map, plan, facts);
    }

    // The unplaced rows of boulders the lookup could not read are dropped (a user's own rows stay wall-less).
    private static void Fail(FeedMapContext map, List<FeedBoulderTouch> lookup, FeedPlacementPlan plan, Exception ex, ILogger logger)
    {
        Fallback(map, lookup, ex, logger);
        FeedBoulderPlacement.Apply(map, plan, []);
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
                AddRow(map, touch, boulder.WallId, boulder.Generation, boulder.IsDraft);
            }
            else if (scope is not null)
            {
                map.AddWithoutActor(FeedRows.Resync(touch.WallId ?? scope.WallId, scope.Label, null, map.Now));
            }
            else
            {
                lookup.Add(touch);
            }
        }

        return lookup;
    }

    private static IQueryable<FeedBoulderFacts> Query(DbContext context, List<Guid> ids)
    {
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
                AddRow(map, touch, fact.WallId, fact.Generation, fact.IsDraft);
            }
            else if (touch.MappingFailed)
            {
                logger.LogError("Feed mapping failed on a part of boulder {BoulderId}, which no longer exists; no feed row is written for it", touch.BoulderId);
            }
        }
    }

    // A part whose mapper threw resyncs the boulder's wall, actorless (the part may be a tick or a rating, and every viewer
    // of the wall sees the resync); any other touch is the boulder's one boulder.changed, credited to the editor.
    private static void AddRow(FeedMapContext map, FeedBoulderTouch touch, Guid wallId, int generation, bool isDraft)
    {
        if (touch.MappingFailed)
        {
            map.AddWithoutActor(FeedRows.Resync(wallId, FeedCollector.MappingFailedReason, null, map.Now));
            return;
        }

        map.Add(FeedBoulderRows.Changed(touch.BoulderId, wallId, generation, isDraft, isDraft));
    }

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
            map.AddWithoutActor(FeedRows.Resync(wall, FeedCollector.MappingFailedReason, null, map.Now));
        }
    }
}
