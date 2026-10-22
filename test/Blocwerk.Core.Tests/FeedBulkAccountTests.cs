// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Blocwerk.Core.Tests.FeedBulkKit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The GDPR side of the feed (design §5.7, PR 1.3): an erasure leaves no stored row naming the user and resyncs their walls,
/// and a merge forgets the source's rows, revokes nothing, and tells the target's devices with a <c>user.resync</c>. Rows
/// that the user's other devices commit during the erasure are caught by the read-committed pass after it. Both write no wall rows of
/// their own saves (a silent account scope), on SQLite and Postgres.
/// </summary>
public sealed class FeedBulkAccountTests
{
    [Fact]
    public Task Erasure_LeavesNoRowNamingTheUser_AndResyncsTheirWalls_Sqlite() => EraseAsync(new WallTestHarness());

    [PostgresFact]
    public Task Erasure_LeavesNoRowNamingTheUser_AndResyncsTheirWalls_Postgres() => EraseAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    [Fact]
    public Task Merge_ForgetsTheSourcesRows_RevokesNothing_AndTellsTheTarget_Sqlite() => MergeAsync(new WallTestHarness());

    [PostgresFact]
    public Task Merge_ForgetsTheSourcesRows_RevokesNothing_AndTellsTheTarget_Postgres() => MergeAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    [Fact]
    public async Task TheScrubAfterTheErasure_CatchesRowsWrittenMeanwhile_AndNeverThrows()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var late = Guid.NewGuid();
        await SeedAsync(
            h,
            SeedRow(h.WallId, FeedKinds.CommentAdded, Guid.NewGuid(), actor: late),
            SeedRow(null, FeedKinds.TickMine, Guid.NewGuid(), actor: late, audienceUser: late));

        await using (var db = h.CreateContext())
        {
            await FeedAccountRows.ScrubCommittedAsync(db, late, NullLogger.Instance);
        }

        var row = Assert.Single(await RowsAsync(h));
        Assert.Equal(GhostUser.Id, row.ActorUserId);

        var logger = new CapturingLogger<FeedBulkAccountTests>();
        await using (var broken = h.CreateContext())
        {
            await broken.DisposeAsync();
            await FeedAccountRows.ScrubCommittedAsync(broken, late, logger);
        }

        Assert.Contains(logger.Lines, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task ASilentScope_DropsWallRows_AndKeepsUserRows()
    {
        using var h = new WallTestHarness();
        var boulder = await StructureFeedKit.SeedBoulderAsync(h, (await h.SeedWallAsync(holdCount: 1))[0].Id);
        await ClearAsync(h);

        await using (var db = StructureFeedKit.Context(h))
        using (FeedScope.BeginAccount("probe"))
        {
            db.BoulderRatings.Add(new BoulderRating { BoulderId = boulder.Id, UserId = h.Owner.Id, Stars = 3 });
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await RowsAsync(h));
        Assert.Equal((FeedKinds.RatingMine, FeedAudience.User, h.Owner.Id), (row.Kind, row.Audience, row.AudienceUserId!.Value));
    }

    private static async Task EraseAsync(WallTestHarness h)
    {
        using var owned = h;
        var fixture = await DeletionFixture.CreateAsync(h);
        var leaver = fixture.LeaverId;
        await ClearAsync(h);
        await SeedAsync(
            h,
            SeedRow(h.WallId, FeedKinds.CommentAdded, Guid.NewGuid(), actor: leaver),
            SeedRow(null, FeedKinds.TickMine, fixture.BoulderId, actor: leaver, audienceUser: leaver),
            SeedRow(h.WallId, FeedKinds.MemberChanged, leaver, actor: h.Owner.Id),
            SeedRow(h.WallId, FeedKinds.HoldChanged, Guid.NewGuid(), actor: h.Owner.Id));
        var signal = new RecordingFeedSignal();
        var service = new AccountDeletionService(
            new FeedDbContextFactory(h, signal), Substitute.For<Abstractions.IBetaVideoStorage>(), h.CurrentUser, NullLogger<AccountDeletionService>.Instance);

        Assert.True(await service.DeleteAsync(leaver));

        var rows = await RowsAsync(h);
        Assert.DoesNotContain(rows, r => r.ActorUserId == leaver || r.AudienceUserId == leaver || r.EntityId == leaver);
        Assert.Equal(GhostUser.Id, Assert.Single(rows, r => r.Kind == FeedKinds.CommentAdded).ActorUserId);
        Assert.Contains(rows, r => r.Kind == FeedKinds.HoldChanged && r.ActorUserId == h.Owner.Id);
        var resync = Assert.Single(rows, r => r.Kind == FeedKinds.WallResync);
        Assert.Equal((h.WallId, null), (resync.WallId!.Value, resync.ActorUserId));
        Assert.Equal(FeedAccountRows.EraseReason, Field(resync, "reason"));
        Assert.DoesNotContain(rows, r => r.Kind is FeedKinds.UserProfile or FeedKinds.AccessRevoked);
        Assert.Contains(signal.Woken, w => w.WallId == h.WallId);
    }

    private static async Task MergeAsync(WallTestHarness h)
    {
        using var owned = h;
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await StructureFeedKit.SeedBoulderAsync(h, holds[0].Id);
        var source = await h.AddMemberAsync("source@test", WallRole.Member);
        var target = new User { Identifier = "target@test", DisplayName = "Target" };
        await using (var db = h.CreateContext())
        {
            db.Users.Add(target);
            db.BoulderRatings.Add(new BoulderRating { BoulderId = boulder.Id, UserId = source.Id, Stars = 4 });
            await db.SaveChangesAsync();
        }

        await ClearAsync(h);
        await SeedAsync(h, SeedRow(h.WallId, FeedKinds.CommentAdded, Guid.NewGuid(), actor: source.Id));

        await new AccountMergeService(new FeedDbContextFactory(h, new RecordingFeedSignal()), NullLogger<AccountMergeService>.Instance)
            .MergeUsersAsync(source.Id, target.Id);

        var rows = await RowsAsync(h);
        Assert.DoesNotContain(rows, r => r.ActorUserId == source.Id || r.AudienceUserId == source.Id || r.EntityId == source.Id);
        Assert.DoesNotContain(rows, r => r.Kind == FeedKinds.AccessRevoked);
        Assert.DoesNotContain(rows, r => r.Kind == FeedKinds.CommentAdded);
        Assert.Contains(rows, r => r.Kind == FeedKinds.RatingMine && r.AudienceUserId == target.Id);
        var mine = Assert.Single(rows, r => r.Kind == FeedKinds.UserResync);
        Assert.Equal((FeedAudience.User, target.Id, null), (mine.Audience, mine.AudienceUserId!.Value, mine.WallId));
        var resync = Assert.Single(rows, r => r.Kind == FeedKinds.WallResync);
        Assert.Equal(FeedAccountRows.MergeReason, Field(resync, "reason"));
        Assert.DoesNotContain(rows, r => r.Kind == FeedKinds.MemberChanged);
    }

    private static WallChange SeedRow(Guid? wallId, string kind, Guid entityId, Guid? actor, Guid? audienceUser = null) => new()
    {
        WallId = wallId,
        Kind = kind,
        EntityId = entityId,
        Audience = audienceUser is null ? FeedAudience.Wall : FeedAudience.User,
        AudienceUserId = audienceUser,
        ActorUserId = actor,
    };

    private static async Task SeedAsync(WallTestHarness h, params WallChange[] rows)
    {
        await using var db = h.CreateContext();
        db.WallChanges.AddRange(rows);
        await db.SaveChangesAsync();
    }
}
