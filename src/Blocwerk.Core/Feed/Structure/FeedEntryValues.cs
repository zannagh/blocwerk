// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Structure;

/// <summary>Reads a changed entry the way a mapper needs it: a deleted row by its original values.</summary>
internal static class FeedEntryValues
{
    /// <summary>The property's value as the row stands after the save, or as it stood before a delete.</summary>
    public static T Value<T>(EntityEntry entry, string property) =>
        (T)(entry.State == EntityState.Deleted ? entry.OriginalValues[property] : entry.CurrentValues[property])!;

    /// <summary>The property's value before the save (its current value for an added row).</summary>
    public static T Original<T>(EntityEntry entry, string property) =>
        (T)(entry.State == EntityState.Added ? entry.CurrentValues[property] : entry.OriginalValues[property])!;

    /// <summary>True when a modified entry changed nothing but <paramref name="properties"/> (columns no viewer sees).</summary>
    public static bool OnlyModified(EntityEntry entry, IReadOnlySet<string> properties) =>
        entry.State == EntityState.Modified
        && entry.Properties.Where(p => p.IsModified).All(p => properties.Contains(p.Metadata.Name));
}
