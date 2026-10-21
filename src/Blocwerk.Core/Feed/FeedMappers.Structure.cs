// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed.Structure;

namespace Blocwerk.Core.Feed;

/// <summary>The wall-structure family (PR 1.2b): wall, members, segments, panels, links, volumes, holds and boulders.</summary>
public static partial class FeedMappers
{
    static partial void AddStructure(List<IFeedMapper> mappers)
    {
        mappers.Add(new WallFeedMapper());
        mappers.Add(new WallMemberFeedMapper());
        mappers.Add(new WallPartFeedMapper());
        mappers.Add(new HoldFeedMapper());
        mappers.Add(new BoulderFeedMapper());
        mappers.Add(new BoulderPartFeedMapper());
    }
}
