// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>
/// The boulders of a save's unplaced rows (<see cref="FeedBoulderPlacement"/>): those already known from the change
/// tracker, and those the resolver's one lookup must read.
/// </summary>
internal sealed record FeedPlacementPlan(Dictionary<Guid, FeedBoulderFacts> Known, IReadOnlyList<Guid> Missing)
{
    public static FeedPlacementPlan None => new([], []);
}
