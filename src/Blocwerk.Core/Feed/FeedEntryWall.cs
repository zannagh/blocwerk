// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The wall of a changed entry without its mapper: the wall itself, or the entry's <c>WallId</c> property (the original
/// value of a deleted row). Used only when a mapper threw, to aim the fallback <c>wall.resync</c>.
/// </summary>
internal static class FeedEntryWall
{
    public static Guid? Of(EntityEntry entry)
    {
        if (entry.Entity is Wall wall)
        {
            return wall.Id;
        }

        var property = entry.Metadata.FindProperty(nameof(Hold.WallId));
        if (property is null)
        {
            return null;
        }

        var value = entry.State == EntityState.Deleted ? entry.OriginalValues[property] : entry.CurrentValues[property];
        return value is Guid id && id != Guid.Empty ? id : null;
    }
}
