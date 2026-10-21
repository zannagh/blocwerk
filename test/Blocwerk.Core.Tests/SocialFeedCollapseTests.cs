// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The social rows and the collapse: a user's own rows survive a scope and a bulk save (a wall resync would not reach
/// them), and the resync a collapse writes names no actor.
/// </summary>
public sealed class SocialFeedCollapseTests : IDisposable
{
    private readonly SocialFeedFixture fixture = new();

    [Fact]
    public async Task InsideAScope_TheMineRowsSurvive_AndTheResyncNamesNobody()
    {
        var boulder = await fixture.SeedAsync();
        using (FeedScope.Begin(fixture.WallId, "merge"))
        {
            await using var db = fixture.Context(fixture.OwnerId);
            db.Add(new Attempt { BoulderId = boulder, UserId = fixture.OwnerId, Type = AttemptType.Send });
            db.Add(new BoulderRating { BoulderId = boulder, UserId = fixture.OwnerId, Stars = 3 });
            db.Add(new BoulderFavorite { BoulderId = boulder, UserId = fixture.OwnerId });
            await db.SaveChangesAsync();
        }

        var rows = await fixture.RowsAsync();
        AssertMineSurvived(rows, 3);
        var resync = Assert.Single(rows, r => r.Kind == FeedKinds.WallResync);
        Assert.Null(resync.ActorUserId);
    }

    [Fact]
    public async Task InABulkSave_TheMineRowsSurvive_AndTheResyncNamesNobody()
    {
        await fixture.SeedAsync();
        var boulders = new List<Guid>();
        for (var i = 0; i <= FeedCollapse.MaxRowsPerWall; i++)
        {
            boulders.Add(await fixture.AddBoulderAsync());
        }

        await using (var db = fixture.Context(fixture.OwnerId))
        {
            db.AddRange(boulders.Select(b => new Attempt { BoulderId = b, UserId = fixture.OwnerId, Type = AttemptType.Flash }));
            db.Add(new BoulderRating { BoulderId = boulders[0], UserId = fixture.OwnerId, Stars = 1 });
            db.Add(new BoulderFavorite { BoulderId = boulders[0], UserId = fixture.OwnerId });
            await db.SaveChangesAsync();
        }

        var rows = await fixture.RowsAsync();
        Assert.Equal(boulders.Count, rows.Count(r => r.Kind == FeedKinds.TickMine));
        AssertMineSurvived(rows, boulders.Count + 2);
        var resync = Assert.Single(rows, r => r.Kind == FeedKinds.WallResync);
        Assert.Equal(((Guid?)fixture.WallId, (Guid?)null), (resync.WallId, resync.ActorUserId));
        Assert.DoesNotContain(rows, r => r.Kind == FeedKinds.BoulderStats);
    }

    public void Dispose() => fixture.Dispose();

    private void AssertMineSurvived(List<WallChange> rows, int count)
    {
        var mine = rows.Where(r => r.Audience == FeedAudience.User).ToList();
        Assert.Equal(count, mine.Count);
        Assert.All(mine, r => Assert.Equal(fixture.OwnerId, r.AudienceUserId));
        Assert.Contains(mine, r => r.Kind == FeedKinds.TickMine);
        Assert.Contains(mine, r => r.Kind == FeedKinds.RatingMine);
        Assert.Contains(mine, r => r.Kind == FeedKinds.FavoriteChanged);
    }
}
