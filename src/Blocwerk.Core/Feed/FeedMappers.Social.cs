// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed.Social;

namespace Blocwerk.Core.Feed;

/// <summary>The social family's registration (PR 1.2c).</summary>
public static partial class FeedMappers
{
    static partial void AddSocial(List<IFeedMapper> mappers) => mappers.AddRange(SocialFeedMappers.Create());
}
