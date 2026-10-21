// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Turns one changed entity of a family (design §5.4) into feed rows. Runs in <c>SavingChanges</c>, while the entry still
/// has its state and original values, on the writer's thread: pure, cheap, and without queries (counts and content are
/// read at delivery, never here). The rows carry ids only. A mapper that knows only the boulder leaves <c>WallId</c> unset;
/// the resolver then places the row through the boulder (<see cref="FeedBoulderPlacement"/>).
/// </summary>
public interface IFeedMapper
{
    /// <summary>Gets the entity types this mapper covers; the model-coverage guard reads them.</summary>
    IReadOnlyCollection<Type> EntityTypes { get; }

    /// <summary>Adds the rows for <paramref name="entry"/> (Added, Modified or Deleted) through <see cref="FeedMapContext.Add"/>.</summary>
    void Map(EntityEntry entry, FeedMapContext context);
}
