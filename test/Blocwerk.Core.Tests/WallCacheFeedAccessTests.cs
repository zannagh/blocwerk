// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Web.State;
using Blocwerk.Web.State.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The circuit's subscription follows its access (PR 1.4): a demotion resubscribes with the new role's audiences, a
/// removal leaves the wall, a promotion, an ownership change or a resync (which write no access event) re-reads the
/// viewer, and a
/// disposed circuit ignores the late delivery the hub may still hand it.
/// </summary>
public sealed class WallCacheFeedAccessTests : IAsyncLifetime
{
    private readonly CircuitCacheFixture f = new();

    private Guid Me => f.Harness.Owner.Id;

    public async Task InitializeAsync()
    {
        await f.StartAsync();
        await f.Cache.GetWallAsync(f.WallId);
    }

    public async Task DisposeAsync() => await f.DisposeAsync();

    [Fact]
    public async Task ARevokeWithARole_ResubscribesWithThatRolesAudiences()
    {
        Assert.Contains(FeedAudience.Setters, f.Link.ViewerOf(f.WallId)!.Audiences);

        await f.DeliverAsync(Revoked(new MemberPayload(nameof(WallRole.Member))));
        await CircuitCacheFixture.WaitUntilAsync(() => f.Link.ViewerOf(f.WallId) is { } v && !v.Audiences.Contains(FeedAudience.Setters));

        Assert.True(f.Link.ViewerOf(f.WallId)!.SameAs(FeedViewer.Member(Me)));
        Assert.Empty(f.Lost);
        await CircuitCacheFixture.WaitUntilAsync(() => !f.Store.Walls.TryPeek(f.WallId, out _));
    }

    [Fact]
    public async Task ARevokeWithoutARole_LeavesTheWall_AndNothingMoreArrives()
    {
        f.Fake.Viewer = null; // the membership is gone
        await f.DeliverAsync(Revoked(null));
        await CircuitCacheFixture.WaitUntilAsync(() => f.Lost.Count == 1);

        Assert.Equal(f.WallId, f.Lost[0]);
        Assert.Null(f.Link.ViewerOf(f.WallId));
        Assert.Contains(f.Changes, c => c.Scope == WallCacheChangeScope.WallList);
        Assert.False(f.Store.Walls.TryPeek(f.WallId, out _));

        var count = f.Deliveries.Count;
        await f.CommitAsync(true, f.Row(FeedKinds.WallResync));
        await Task.Delay(300);
        Assert.Equal(count, f.Deliveries.Count);
        Assert.Equal(0, f.Hub.TrackedWallCount);
    }

    [Fact]
    public async Task AMemberChangeOfMyself_RereadsTheViewer_AndAPromotionResubscribes()
    {
        f.Fake.Viewer = FeedViewer.Member(Me);
        await f.DeliverAsync(f.Row(FeedKinds.MemberChanged, Me, new MemberPayload("Member"), FeedAudience.Members));
        await CircuitCacheFixture.WaitUntilAsync(() => f.Link.ViewerOf(f.WallId)!.SameAs(FeedViewer.Member(Me)));

        f.Fake.Viewer = FeedViewer.Setter(Me); // promoted to moderator: no access event is written
        await f.DeliverAsync(f.Row(FeedKinds.MemberChanged, Me, new MemberPayload("Moderator"), FeedAudience.Members));
        await CircuitCacheFixture.WaitUntilAsync(() => f.Link.ViewerOf(f.WallId)!.SameAs(FeedViewer.Setter(Me)));

        await f.DeliverAsync(f.Row("setters-only", audience: FeedAudience.Setters));
        Assert.Equal("setters-only", f.Deliveries[^1].Events.Single().Kind);
    }

    [Fact]
    public async Task AMemberChangeOfSomeoneElse_DoesNotRereadTheViewer()
    {
        var before = f.Fake.Resolved;
        await f.DeliverAsync(f.Row(FeedKinds.MemberChanged, Guid.NewGuid(), new MemberPayload("Admin"), FeedAudience.Members));
        await Task.Delay(200);

        Assert.Equal(before, f.Fake.Resolved);
    }

    [Fact]
    public async Task AResync_RereadsTheViewer_AndLeavesWhenTheViewerIsGone()
    {
        f.Fake.Viewer = null; // erased: the erasure writes only wall.resync, no access event
        await f.DeliverAsync(f.Row(FeedKinds.WallResync));
        await CircuitCacheFixture.WaitUntilAsync(() => f.Lost.Count == 1);

        Assert.Equal(f.WallId, f.Lost[0]);
        Assert.Null(f.Link.ViewerOf(f.WallId));
    }

    [Fact]
    public async Task AResync_RereadsTheViewer_AndAPromotionInsideABulkOperationResubscribes()
    {
        f.Fake.Viewer = FeedViewer.Member(Me);
        await f.DeliverAsync(f.Row(FeedKinds.MemberChanged, Me, new MemberPayload("Member"), FeedAudience.Members));
        await CircuitCacheFixture.WaitUntilAsync(() => f.Link.ViewerOf(f.WallId)!.SameAs(FeedViewer.Member(Me)));

        f.Fake.Viewer = FeedViewer.Setter(Me); // promoted inside a scope: the member.changed collapsed into the resync
        await f.DeliverAsync(f.Row(FeedKinds.WallResync));
        await CircuitCacheFixture.WaitUntilAsync(() => f.Link.ViewerOf(f.WallId)!.SameAs(FeedViewer.Setter(Me)));
        Assert.Empty(f.Lost);
    }

    [Fact]
    public void AResyncAfterARemoval_StillLeaves_AndAResyncAloneRereads()
    {
        var revoked = new FeedEnvelope { Cursor = "c1", Kind = FeedKinds.AccessRevoked, WallId = f.WallId };
        var resync = new FeedEnvelope { Cursor = "c2", Kind = FeedKinds.WallResync, WallId = f.WallId };

        Assert.Equal(WallFeedAccessKind.Leave, WallFeedAccess.Decide([revoked, resync], Me).Kind);
        Assert.Equal(WallFeedAccessKind.Reread, WallFeedAccess.Decide([resync], Me).Kind);
    }

    [Fact]
    public async Task AWallUpdate_RereadsTheViewer_ForAnOwnershipTransfer()
    {
        var before = f.Fake.Resolved;
        f.Fake.Viewer = FeedViewer.Member(Me); // ownership moved away; still a member
        await f.DeliverAsync(f.Row(FeedKinds.WallUpdated, f.WallId, new WallPayload(0, OwnerChanged: true)));

        await CircuitCacheFixture.WaitUntilAsync(() => f.Link.ViewerOf(f.WallId)!.SameAs(FeedViewer.Member(Me)));
        Assert.True(f.Fake.Resolved > before);
    }

    [Fact]
    public async Task AfterDispose_ALateDeliveryIsIgnored_AndTheHubForgetsTheWall()
    {
        var slot = (await f.Link.EnsureAsync(f.WallId))!;
        f.Cache.Dispose();
        f.Link.Dispose();

        var cursor = slot.Start!.Value;
        slot.OnFeed(new FeedDelivery(f.WallId, [], cursor, Resync: true)); // the one call the hub may still make

        Assert.True(f.Store.Walls.TryPeek(f.WallId, out _));
        Assert.Empty(f.Changes);
        await CircuitCacheFixture.WaitUntilAsync(() => f.Hub.TrackedWallCount == 0); // retired by Dispose's continuation
        Assert.Null(await f.Link.EnsureAsync(f.WallId));
    }

    [Fact]
    public async Task AHandlerThatThrows_MissesThatBatch_ButStaysSubscribed()
    {
        var throws = true;
        f.Cache.Changed += _ =>
        {
            if (throws)
            {
                throws = false;
                throw new InvalidOperationException("page bug");
            }
        };

        await f.CommitAsync(true, f.Row(FeedKinds.MemberChanged, Guid.NewGuid(), audience: FeedAudience.Members));
        await CircuitCacheFixture.WaitUntilAsync(() => !throws);
        await f.Cache.GetWallAsync(f.WallId);

        await f.DeliverAsync(f.Row(FeedKinds.MemberChanged, Guid.NewGuid(), audience: FeedAudience.Members));
        Assert.False(f.Store.Walls.TryPeek(f.WallId, out _));
    }

    private WallChange Revoked(MemberPayload? role) =>
        f.Row(FeedKinds.AccessRevoked, f.WallId, role, FeedAudience.User, user: Me);
}
