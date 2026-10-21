// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Feed;

/// <summary>
/// A boulder whose parts changed in a save (<see cref="FeedMapContext.TouchBoulder"/>): the boulder when the context tracks
/// it, and its wall when a part knew it. <see cref="MappingFailed"/> marks a part whose mapper threw: the boulder's wall
/// then gets a <c>mapping-failed</c> resync instead of a <c>boulder.changed</c>.
/// </summary>
public sealed record FeedBoulderTouch(Guid BoulderId, Boulder? Boulder, Guid? WallId, bool MappingFailed = false);
