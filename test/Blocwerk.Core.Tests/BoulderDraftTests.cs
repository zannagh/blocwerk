using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Covers draft visibility and the derived foothold mode, since a leak here would
/// expose unfinished boulders to the whole wall.
/// </summary>
public class BoulderDraftTests
{
    [Fact]
    public async Task Draft_IsHiddenFromOtherMembers_ButVisibleToItsCreator()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);

        var draft = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "WIP", null, [new BoulderHoldInput(holds[0].Id)], isDraft: true);

        var mine = await h.BoulderService.GetBouldersForWallAsync(h.WallId);
        Assert.Contains(mine, b => b.Id == draft.Id);

        // A second member of the same wall must not see it.
        var other = new User { Identifier = "other@test", DisplayName = "Other" };
        await using (var db = h.CreateContext())
        {
            db.Users.Add(other);
            db.WallMembers.Add(new WallMember { WallId = h.WallId, UserId = other.Id, Role = WallRole.Member });
            await db.SaveChangesAsync();
        }

        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromResult(other));
        var theirs = await h.BoulderService.GetBouldersForWallAsync(h.WallId);
        Assert.DoesNotContain(theirs, b => b.Id == draft.Id);
    }

    [Fact]
    public async Task PublishBoulder_MakesItVisible_AndLogsCreationOnce()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);

        var draft = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "WIP", null, [new BoulderHoldInput(holds[0].Id)], isDraft: true);

        // Drafting stays out of the activity feed.
        await h.ActivityLog.DidNotReceive().LogAsync(
            Arg.Any<Guid>(), Arg.Any<Guid?>(), ActivityType.BoulderCreated, Arg.Any<string?>());

        await h.BoulderService.PublishBoulderAsync(draft.Id);

        await h.ActivityLog.Received(1).LogAsync(
            h.WallId, draft.Id, ActivityType.BoulderCreated, "WIP");

        await using var check = h.CreateContext();
        var published = await check.Boulders.FirstAsync(b => b.Id == draft.Id);
        Assert.False(published.IsDraft);
        Assert.NotNull(published.PublishedAt);
    }

    [Fact]
    public async Task PublishBoulder_Throws_WhenNoHoldsSelected()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 2);

        var draft = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Empty", null, [], isDraft: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.BoulderService.PublishBoulderAsync(draft.Id));
    }

    [Fact]
    public async Task PublishBoulder_Throws_ForNonCreator()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        var draft = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "WIP", null, [new BoulderHoldInput(holds[0].Id)], isDraft: true);

        var other = new User { Identifier = "other@test", DisplayName = "Other" };
        await using (var db = h.CreateContext())
        {
            db.Users.Add(other);
            db.WallMembers.Add(new WallMember { WallId = h.WallId, UserId = other.Id, Role = WallRole.Admin });
            await db.SaveChangesAsync();
        }

        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromResult(other));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.BoulderService.PublishBoulderAsync(draft.Id));
    }

    /// <summary>
    /// Usage marks no longer drive the mode: the boulder's explicit rules do. A hand-only
    /// mark still leaves the kickboard in charge as long as hands follow feet.
    /// </summary>
    [Theory]
    [InlineData(HoldUsage.HandAndFoot)]
    [InlineData(HoldUsage.HandOnly)]
    public async Task FootholdMode_StaysAllKickboard_WhileHandsFollowFeet(HoldUsage usage)
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);

        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId,
            "B",
            null,
            [new BoulderHoldInput(holds[0].Id, HoldType.Start), new BoulderHoldInput(holds[1].Id, HoldType.Top, usage)]);

        await using var check = h.CreateContext();
        var saved = await check.Boulders
            .Include(b => b.BoulderHolds)
            .FirstAsync(b => b.Id == boulder.Id);

        Assert.Equal(FootholdMode.AllKickboard, saved.FootholdMode);
        Assert.Contains(saved.BoulderHolds, bh => bh.HoldId == holds[1].Id && bh.Usage == usage);
    }

    [Fact]
    public async Task ReviseBoulder_RemapsHistoricBoulder_AndUpdatesNameAndGrade()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 3);

        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Old name", "6A", [new BoulderHoldInput(holds[0].Id)]);

        await using (var db = h.CreateContext())
        {
            var b = await db.Boulders.FirstAsync(x => x.Id == boulder.Id);
            b.IsHistoric = true;
            b.NeedsReview = true;
            await db.SaveChangesAsync();
        }

        await h.BoulderService.ReviseBoulderAsync(
            boulder.Id,
            [new BoulderHoldInput(holds[2].Id, HoldType.Start, HoldUsage.FootOnly)],
            name: "New name",
            grade: "6B",
            handsFollowFeet: false);

        await using var check = h.CreateContext();
        var revised = await check.Boulders
            .Include(b => b.BoulderHolds)
            .FirstAsync(b => b.Id == boulder.Id);

        Assert.False(revised.IsHistoric);
        Assert.False(revised.NeedsReview);
        Assert.Equal("New name", revised.Name);
        Assert.Equal("6B", revised.Grade);
        Assert.Equal(FootholdMode.DefinedOnly, revised.FootholdMode);
        Assert.Equal(holds[2].Id, Assert.Single(revised.BoulderHolds).HoldId);
    }

    [Fact]
    public async Task ReviseBoulder_IsAllowedForDrafts()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);

        var draft = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "WIP", null, [new BoulderHoldInput(holds[0].Id)], isDraft: true);

        await h.BoulderService.ReviseBoulderAsync(
            draft.Id, [new BoulderHoldInput(holds[1].Id)], name: "WIP v2");

        await using var check = h.CreateContext();
        var saved = await check.Boulders.FirstAsync(b => b.Id == draft.Id);

        Assert.True(saved.IsDraft);
        Assert.Equal("WIP v2", saved.Name);
    }

    [Fact]
    public async Task ReviseBoulder_Throws_ForBoulderThatIsNeitherHistoricNorDraft()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Live", null, [new BoulderHoldInput(holds[0].Id)]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.BoulderService.ReviseBoulderAsync(boulder.Id, [new BoulderHoldInput(holds[1].Id)]));
    }
}
