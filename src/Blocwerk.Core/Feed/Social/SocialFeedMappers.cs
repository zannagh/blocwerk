// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed.Social;

/// <summary>The social mapper family (PR 1.2c, design §5.4): comments, videos, stats, gallery, activity, "mine", profile, 3D.</summary>
public static class SocialFeedMappers
{
    /// <summary>Creates the family's mappers.</summary>
    public static IReadOnlyList<IFeedMapper> Create() =>
    [
        new CommentFeedMapper(),
        new BetaVideoFeedMapper(),
        new BoulderStatsFeedMapper(),
        new FavoriteFeedMapper(),
        new GalleryFeedMapper(),
        new ActivityLogFeedMapper(),
        new View3DFeedMapper(),
        new UserProfileFeedMapper(),
    ];
}
