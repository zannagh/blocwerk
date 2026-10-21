// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The wall of a changed entry without its mapper, to aim the fallback <c>wall.resync</c> when a mapper threw: the wall
/// itself, the entry's <c>WallId</c>, or, for boulder-scoped rows (comments, boulder holds, ticks, ratings, videos,
/// setters, favourites, grade proposals), the wall of its <c>BoulderId</c>. Values of a deleted row are its original
/// ones. The boulder is taken from the tracker when it is there (also one being added or deleted in the same save),
/// else looked up with one query. That query runs only on this failure path.
/// </summary>
internal static class FeedEntryWall
{
    public static Guid? Of(EntityEntry entry)
    {
        if (entry.Entity is Wall wall)
        {
            return wall.Id;
        }

        if (Value(entry, nameof(Hold.WallId)) is { } wallId)
        {
            return wallId;
        }

        return Value(entry, nameof(BoulderComment.BoulderId)) is { } boulderId ? WallOfBoulder(entry.Context, boulderId) : null;
    }

    private static Guid? WallOfBoulder(DbContext context, Guid boulderId)
    {
        var tracked = context.ChangeTracker.Entries<Boulder>().FirstOrDefault(e => e.Entity.Id == boulderId);
        if (tracked is not null)
        {
            return Value(tracked, nameof(Boulder.WallId));
        }

        return context.Set<Boulder>().AsNoTracking()
            .Where(b => b.Id == boulderId)
            .Select(b => (Guid?)b.WallId)
            .FirstOrDefault();
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
