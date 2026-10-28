// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>
/// What a <c>boulder.changed</c> row, or a row placed on a boulder, needs of a boulder the save did not track, read in one
/// lookup per save.
/// </summary>
internal sealed record FeedBoulderFacts(Guid Id, Guid WallId, bool IsDraft, int Generation, bool IsArchived);
