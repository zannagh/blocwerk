// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Maps one save's changed entries to feed rows and collapses them (<see cref="FeedCollapse"/>). A mapper that throws
/// never fails the save: its partial rows are dropped and the entry's wall gets a coarse <c>wall.resync</c> instead.
/// </summary>
internal static class FeedCollector
{
    /// <summary>The resync reason when a mapper threw.</summary>
    public const string MappingFailedReason = "mapping-failed";

    public static IReadOnlyList<WallChange> Collect(DbContext context, FeedMapperRegistry registry, ILogger logger)
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

        return FeedCollapse.Apply(map.Rows, scope, actor, now);
    }

    // The entry's wall gets a resync whenever it can be known; only an entry of no knowable wall writes nothing, and that
    // is logged as such.
    private static void Fallback(EntityEntry entry, IFeedMapper mapper, Exception error, FeedMapContext map, FeedScopeState? scope, ILogger logger)
    {
        Guid? wallId;
        try
        {
            wallId = FeedEntryWall.Of(entry) ?? scope?.WallId;
        }
        catch (Exception lookup)
        {
            logger.LogError(lookup, "Resolving the wall of {Entity} for a feed resync failed", entry.Metadata.ClrType.Name);
            wallId = scope?.WallId;
        }

        if (wallId is not { } wall)
        {
            logger.LogError(error, "Feed mapper {Mapper} failed on {Entity} ({State}) and its wall is unknown; no feed row is written for it",
                mapper.GetType().Name, entry.Metadata.ClrType.Name, entry.State);
            return;
        }

        logger.LogError(error, "Feed mapper {Mapper} failed on {Entity} ({State}); writing a resync of wall {WallId} instead",
            mapper.GetType().Name, entry.Metadata.ClrType.Name, entry.State, wall);
        map.Add(FeedRows.Resync(wall, MappingFailedReason, map.ActorUserId, map.Now));
    }
}
