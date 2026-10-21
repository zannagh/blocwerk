// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
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
                    var wallId = FeedEntryWall.Of(entry) ?? scope?.WallId;
                    logger.LogError(ex, "Feed mapper {Mapper} failed on {Entity} ({State}); writing a resync of wall {WallId} instead",
                        mapper.GetType().Name, entry.Metadata.ClrType.Name, entry.State, wallId);
                    if (wallId is { } wall)
                    {
                        map.Add(FeedRows.Resync(wall, MappingFailedReason, actor, now));
                    }
                }
            }
        }

        return FeedCollapse.Apply(map.Rows, scope, actor, now);
    }
}
