// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Maps one save's changed entries to feed rows, resolves what the mappers could not know (<see cref="FeedBoulderResolver"/>,
/// <see cref="FeedResolve"/>) and collapses them (<see cref="FeedCollapse"/>). A mapper that throws
/// never fails the save: its partial rows are dropped and the entry's wall gets a coarse <c>wall.resync</c> instead.
/// </summary>
internal static class FeedCollector
{
    /// <summary>The resync reason when a mapper threw.</summary>
    public const string MappingFailedReason = "mapping-failed";

    public static IReadOnlyList<WallChange> Collect(DbContext context, FeedMapperRegistry registry, ILogger logger)
    {
        var map = Map(context, registry, logger);
        FeedBoulderResolver.Resolve(context, map, logger);

        // With async false the social step's lookups run synchronously and the task is already complete.
        return Finish(map, FeedResolve.ApplyAsync(context, map.Rows, logger, async: false, CancellationToken.None).GetAwaiter().GetResult());
    }

    public static async Task<IReadOnlyList<WallChange>> CollectAsync(
        DbContext context, FeedMapperRegistry registry, ILogger logger, CancellationToken ct)
    {
        var map = Map(context, registry, logger);
        await FeedBoulderResolver.ResolveAsync(context, map, logger, ct);
        return Finish(map, await FeedResolve.ApplyAsync(context, map.Rows, logger, async: true, ct));
    }

    private static IReadOnlyList<WallChange> Finish(FeedMapContext map, IReadOnlyList<WallChange> resolved)
    {
        var rows = FeedCollapse.Apply(resolved, FeedScope.Active, map.Now);
        FeedAudienceScope.Apply(rows);
        return rows;
    }

    private static FeedMapContext Map(DbContext context, FeedMapperRegistry registry, ILogger logger)
    {
        var actor = context is BlocwerkDbContext { CurrentUserId: var id } && id != Guid.Empty ? id : (Guid?)null;
        var now = DateTimeOffset.UtcNow;
        var map = new FeedMapContext(context, actor, now);
        var scope = FeedScope.Active;

        // Snapshot first: the rows this pass adds must never be mapped themselves.
        var entries = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => e.Entity is not WallChange)
            .ToList();
        foreach (var entry in entries)
        {
            foreach (var mapper in registry.For(entry.Metadata.ClrType))
            {
                var mark = map.Rows.Count;
                try
                {
                    mapper.Map(entry, map);
                }
                catch (Exception ex)
                {
                    map.TruncateTo(mark);
                    Fallback(entry, mapper, ex, map, scope, logger);
                }
            }
        }

        return map;
    }

    // The entry's wall gets a resync whenever it can be known. A boulder-scoped entry hands its boulder to the resolver,
    // which finds the wall with the save's other touched boulders; only an entry of no knowable wall writes nothing.
    private static void Fallback(EntityEntry entry, IFeedMapper mapper, Exception error, FeedMapContext map, FeedScopeState? scope, ILogger logger)
    {
        var wallId = FeedEntryWall.Of(entry);
        if (wallId is null && FeedEntryWall.BoulderOf(entry) is { } boulder)
        {
            logger.LogError(error, "Feed mapper {Mapper} failed on {Entity} ({State}); writing a resync of boulder {BoulderId}'s wall instead",
                mapper.GetType().Name, entry.Metadata.ClrType.Name, entry.State, boulder.Id);
            map.FailBoulder(boulder.Id, boulder.Tracked);
            return;
        }

        if ((wallId ?? scope?.WallId) is not { } wall)
        {
            logger.LogError(error, "Feed mapper {Mapper} failed on {Entity} ({State}) and its wall is unknown; no feed row is written for it",
                mapper.GetType().Name, entry.Metadata.ClrType.Name, entry.State);
            return;
        }

        logger.LogError(error, "Feed mapper {Mapper} failed on {Entity} ({State}); writing a resync of wall {WallId} instead",
            mapper.GetType().Name, entry.Metadata.ClrType.Name, entry.State, wall);

        // Actorless: the failed entry may be a tick or a rating, and the resync goes to every viewer of the wall.
        map.AddWithoutActor(FeedRows.Resync(wall, MappingFailedReason, null, map.Now));
    }
}
