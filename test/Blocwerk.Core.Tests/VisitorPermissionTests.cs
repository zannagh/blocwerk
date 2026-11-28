// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Assets;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.Partition;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The visitor role: reads its one wall like a resting kiosk does, and every write refuses it, because a visitor is
/// anonymous and every write needs a user. The services run on the production wall stamp
/// (<see cref="Data.KioskScopedDbContextFactory"/>).
/// </summary>
public class VisitorPermissionTests
{
    [Fact]
    public async Task Visitor_ReadsTheWallAndItsPublishedBoulders_NotDraftsAndNotAnotherWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var otherWall = await VisitFixture.SeedOtherWallAsync(h);
        var published = await VisitFixture.SeedBoulderAsync(h, h.WallId);
        var draft = await VisitFixture.SeedBoulderAsync(h, h.WallId, draft: true);
        var elsewhere = await VisitFixture.SeedBoulderAsync(h, otherWall);
        var (walls, boulders, _) = Services(h);

        Assert.Equal(h.WallId, (await walls.GetWallAsync(h.WallId))!.Id);
        Assert.NotNull(await boulders.GetBoulderAsync(published));
        Assert.Null(await boulders.GetBoulderAsync(draft));
        Assert.Null(await boulders.GetBoulderAsync(elsewhere));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => walls.GetWallAsync(otherWall));
    }

    [Fact]
    public async Task Visitor_CannotLogRateFavouriteMarkOrComment()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var boulder = await VisitFixture.SeedBoulderAsync(h, h.WallId);
        var visitor = VisitFixture.Visitor(h.WallId);
        var factory = VisitFixture.Stamped(h, visitor);
        VisitFixture.GoAnonymous(h);
        var attempts = new AttemptService(factory, h.CurrentUser, NullLogger<AttemptService>.Instance);
        var feedback = new BoulderFeedbackService(factory, h.CurrentUser, NullLogger<BoulderFeedbackService>.Instance);
        var comments = new CommentService(factory, h.CurrentUser, h.ActivityLog, NullLogger<CommentService>.Instance, null, visitor);
        var projects = new BoulderProjectService(factory, h.CurrentUser, visitor, feedback, NullLogger<BoulderProjectService>.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => attempts.LogAttemptAsync(boulder, AttemptType.Send));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => feedback.SetRatingAsync(boulder, 5));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => feedback.SetFavoriteAsync(boulder, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => projects.SetProjectAsync(boulder, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => comments.AddCommentAsync(boulder, "nice"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.BetaVideoService.EnsureCanUploadAsync(boulder));
        await AssertNothingWrittenAsync(h);
    }

    [Fact]
    public async Task Visitor_CannotCreateArchiveOrChangeBouldersHoldsOrTheWall_OrHandOutLinks()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var boulder = await VisitFixture.SeedBoulderAsync(h, h.WallId);
        var (walls, boulders, visitor) = Services(h);

        Assert.False(await boulders.CanCreateAnonymouslyAsync(h.WallId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => boulders.CreateBoulderAsync(h.WallId, "Mine", "6a", []));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => boulders.ArchiveBoulderAsync(boulder));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => walls.AddHoldAsync(h.WallId, 0.5, 0.5, 0.02, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => walls.UpdateWallAsync(h.WallId, "Renamed", null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => walls.GetOrCreateJoinTokenAsync(h.WallId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => VisitFixture.Service(h, new VisitTestClock(VisitFixture.Start), visitor).CreateAsync(h.WallId));

        await using var db = h.CreateContext();
        Assert.Equal(1, await db.Boulders.CountAsync());
        Assert.False((await db.Boulders.SingleAsync()).IsArchived);
        Assert.Equal("Test Wall", (await db.Walls.SingleAsync(w => w.Id == h.WallId)).Name);
        Assert.Empty(await db.WallVisitLinks.ToListAsync());
    }

    [Fact]
    public async Task Visitor_GetsNoAnonymousSettingEvenOnAWallThatAllowsIt()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        await using (var db = h.CreateContext())
        {
            (await db.Walls.SingleAsync()).AllowAnonymousKioskSetting = true;
            await db.SaveChangesAsync();
        }

        var visitor = VisitFixture.Visitor(h.WallId);
        var keys = Substitute.For<IKioskKeyValidator>();
        keys.IsKeyValidAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        await using var read = h.CreateContext();

        Assert.False(await KioskAnonymousSetting.IsAllowedAsync(read, visitor, keys, h.WallId));
    }

    [Fact]
    public async Task Visitor_KeepsNothingOnTheDevice()
    {
        var key = Substitute.For<ICachePartitionKey>();
        key.Key.Returns(new byte[32]);
        var anonymous = Substitute.For<ICurrentUserService>();
        anonymous.GetCurrentUserAsync().Returns(_ => Task.FromException<Entities.User>(new UnauthorizedAccessException()));

        var partition = await new CachePartitionResolver(key, anonymous, VisitFixture.Visitor(Guid.NewGuid())).ResolveAsync();

        Assert.Equal(CachePartitions.None, partition);
    }

    [Fact]
    public async Task WallStamp_PinsTheVisitToItsWall_AndClosesEveryWallOnceItEnded()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var during = VisitFixture.Visitor(h.WallId);
        var ended = Substitute.For<IKioskContext>();
        ended.VisitorWallId.Returns((Guid?)null);
        ended.VisitorExpiresAt.Returns(VisitFixture.Start);

        await using var open = VisitFixture.Stamped(h, during).CreateDbContext();
        await using var closed = VisitFixture.Stamped(h, ended).CreateDbContext();

        Assert.Equal(h.WallId, open.KioskWallId);
        Assert.Equal(Guid.Empty, closed.KioskWallId);
        Assert.Equal(0, await closed.Walls.CountAsync());
    }

    private static (WallService Walls, BoulderService Boulders, IKioskContext Visitor) Services(WallTestHarness h)
    {
        var visitor = VisitFixture.Visitor(h.WallId);
        var factory = VisitFixture.Stamped(h, visitor);
        VisitFixture.GoAnonymous(h);
        var walls = new WallService(factory, h.CurrentUser, h.HoldDetection, h.ActivityLog, NullLogger<WallService>.Instance, visitor);
        var boulders = new BoulderService(factory, h.CurrentUser, h.ActivityLog, NullLogger<BoulderService>.Instance, visitor);
        return (walls, boulders, visitor);
    }

    private static async Task AssertNothingWrittenAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        Assert.Equal(0, await db.Attempts.CountAsync());
        Assert.Equal(0, await db.BoulderRatings.CountAsync());
        Assert.Equal(0, await db.BoulderFavorites.CountAsync());
        Assert.Equal(0, await db.BoulderProjects.CountAsync());
        Assert.Equal(0, await db.BoulderComments.CountAsync());
    }
}
