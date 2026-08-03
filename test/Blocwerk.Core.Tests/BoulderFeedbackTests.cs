using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Covers the boulder rating, favorite and enriched-overview behaviour, since these feed
/// the overview filters and the average shown to every wall member.
/// </summary>
public class BoulderFeedbackTests
{
    [Fact]
    public async Task SetRating_IsUpsert_AndAverageReflectsEveryMember()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        await h.FeedbackService.SetRatingAsync(boulder.Id, 2);

        // Re-rating overwrites rather than adding a second row.
        await h.FeedbackService.SetRatingAsync(boulder.Id, 4);

        var other = await AddMemberAsync(h, "other@test");
        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromResult(other));
        await h.FeedbackService.SetRatingAsync(boulder.Id, 2);

        var info = await h.FeedbackService.GetRatingAsync(boulder.Id);
        Assert.Equal(2, info.Count);
        Assert.Equal(3.0, info.Average);
        Assert.Equal(2, info.MyRating);
    }

    [Fact]
    public async Task SetRating_Throws_ForStarsOutOfRange()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => h.FeedbackService.SetRatingAsync(boulder.Id, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => h.FeedbackService.SetRatingAsync(boulder.Id, 6));
    }

    [Fact]
    public async Task RemoveRating_ClearsOnlyMine()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        await h.FeedbackService.SetRatingAsync(boulder.Id, 5);
        await h.FeedbackService.RemoveRatingAsync(boulder.Id);

        var info = await h.FeedbackService.GetRatingAsync(boulder.Id);
        Assert.Equal(0, info.Count);
        Assert.Null(info.Average);
        Assert.Null(info.MyRating);
    }

    [Fact]
    public async Task ToggleFavorite_FlipsState()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        Assert.False(await h.FeedbackService.IsFavoritedAsync(boulder.Id));
        Assert.True(await h.FeedbackService.ToggleFavoriteAsync(boulder.Id));
        Assert.True(await h.FeedbackService.IsFavoritedAsync(boulder.Id));
        Assert.False(await h.FeedbackService.ToggleFavoriteAsync(boulder.Id));
        Assert.False(await h.FeedbackService.IsFavoritedAsync(boulder.Id));
    }

    [Fact]
    public async Task Rate_Throws_ForNonMember()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        var stranger = new User { Identifier = "stranger@test", DisplayName = "Stranger" };
        await using (var db = h.CreateContext())
        {
            db.Users.Add(stranger);
            await db.SaveChangesAsync();
        }

        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromResult(stranger));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.FeedbackService.SetRatingAsync(boulder.Id, 3));
    }

    [Fact]
    public async Task GetBoulderList_AggregatesAttemptsFavoritesRatings_AndIncludesArchived()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 3);

        var live = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Live", "6A", [new BoulderHoldInput(holds[0].Id)]);
        var archived = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Old", "6B", [new BoulderHoldInput(holds[1].Id)]);

        // Archiving requires the boulder to be historic first.
        await using (var db = h.CreateContext())
        {
            var b = await db.Boulders.FirstAsync(x => x.Id == archived.Id);
            b.IsHistoric = true;
            await db.SaveChangesAsync();
        }

        await h.BoulderService.ArchiveBoulderAsync(archived.Id);

        await h.AttemptService.LogAttemptAsync(live.Id, AttemptType.Send);
        await h.FeedbackService.ToggleFavoriteAsync(live.Id);
        await h.FeedbackService.SetRatingAsync(live.Id, 4);

        var items = await h.FeedbackService.GetBoulderListAsync(h.WallId);
        Assert.Equal(2, items.Count);

        var liveItem = items.Single(i => i.Boulder.Id == live.Id);
        Assert.True(liveItem.AttemptedByMe);
        Assert.True(liveItem.HasSent);
        Assert.True(liveItem.DoneByMe);
        Assert.True(liveItem.IsFavorite);
        Assert.Equal(4.0, liveItem.AverageRating);
        Assert.Equal(1, liveItem.RatingCount);
        Assert.Equal(4, liveItem.MyRating);

        var archivedItem = items.Single(i => i.Boulder.Id == archived.Id);
        Assert.True(archivedItem.Boulder.IsArchived);
        Assert.False(archivedItem.AttemptedByMe);
        Assert.Null(archivedItem.AverageRating);
    }

    [Fact]
    public async Task ArchiveBoulder_Throws_WhenNotHistoric()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Live", null, [new BoulderHoldInput(holds[0].Id)]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.BoulderService.ArchiveBoulderAsync(boulder.Id));
    }

    private static async Task<User> AddMemberAsync(WallTestHarness h, string identifier)
    {
        var user = new User { Identifier = identifier, DisplayName = identifier };
        await using var db = h.CreateContext();
        db.Users.Add(user);
        db.WallMembers.Add(new WallMember { WallId = h.WallId, UserId = user.Id, Role = WallRole.Member });
        await db.SaveChangesAsync();
        return user;
    }
}
