// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The wall of a changed entry without its mapper, to aim the fallback <c>wall.resync</c> when a mapper threw: the wall
/// itself or the entry's <c>WallId</c>. A boulder-scoped row (comments, boulder holds, ticks, ratings, videos, setters,
/// favourites, grade proposals) names its boulder instead (<see cref="BoulderOf"/>), and the
/// <see cref="FeedBoulderResolver"/> finds that boulder's wall with the save's other touched boulders. Values of a
/// deleted row are its original ones. Never queries.
/// </summary>
internal static class FeedEntryWall
{
    public static Guid? Of(EntityEntry entry) =>
        entry.Entity is Wall wall ? wall.Id : Value(entry, nameof(Hold.WallId));

    /// <summary>The entry's boulder id, and the boulder when the context tracks it (navigation fix-up), or null.</summary>
    public static (Guid Id, Boulder? Tracked)? BoulderOf(EntityEntry entry)
    {
        if (Value(entry, nameof(BoulderHold.BoulderId)) is not { } boulderId)
        {
            return null;
        }

        var tracked = entry.References
            .FirstOrDefault(r => r.Metadata.TargetEntityType.ClrType == typeof(Boulder))?.CurrentValue as Boulder;
        return (boulderId, tracked);
    }

    private static Guid? Value(EntityEntry entry, string propertyName)
    {
        var property = entry.Metadata.FindProperty(propertyName);
        if (property is null)
        {
            return null;
        }

        var value = entry.State == EntityState.Deleted ? entry.OriginalValues[property] : entry.CurrentValues[property];
        return value is Guid id && id != Guid.Empty ? id : null;
    }
}
