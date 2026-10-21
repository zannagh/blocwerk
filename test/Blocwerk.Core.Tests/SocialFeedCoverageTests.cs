// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Feed.Social;

namespace Blocwerk.Core.Tests;

/// <summary>The coverage guard after PR 1.2c: nothing of the social family is pending, and each of its entities is decided.</summary>
public sealed class SocialFeedCoverageTests
{
    [Fact]
    public void NoPendingEntry_BelongsTo12c()
    {
        var left = FeedCoverage.Pending.Where(e => e.Value.StartsWith("1.2c", StringComparison.Ordinal)).Select(e => e.Key.Name).ToList();
        Assert.True(left.Count == 0, "Still pending for 1.2c: " + string.Join(", ", left));
    }

    [Fact]
    public void TheSocialEntities_AreMappedByTheProductionMappers()
    {
        var mapped = new FeedMapperRegistry(FeedMappers.Create()).MappedTypes;
        Type[] social =
        [
            typeof(BoulderComment), typeof(BetaVideo), typeof(Attempt), typeof(BoulderRating), typeof(GradeProposal),
            typeof(BoulderFavorite), typeof(WallImage), typeof(ActivityLogEntry), typeof(WallGeometryModel), typeof(User),
        ];

        Assert.All(social, t => Assert.Contains(t, mapped));
        Assert.All(social, t => Assert.Contains(t, SocialFeedMappers.Create().SelectMany(m => m.EntityTypes)));
    }

    [Fact]
    public void ActivityAndClimbingSession_AreReviewedExcluded()
    {
        Assert.Contains(typeof(Activity), FeedCoverage.ReviewedExcluded.Keys);
        Assert.Contains(typeof(ClimbingSession), FeedCoverage.ReviewedExcluded.Keys);
    }
}
