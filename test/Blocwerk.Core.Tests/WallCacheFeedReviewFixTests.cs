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
/// The PR 1.4 review fixes: in-flight loads, the dispose race, batches cut at a revoke, the last access event winning, the
/// owner-changed flag, released and denied slots, and the wall routes left on lost access.
/// </summary>
public sealed class WallCacheFeedReviewFixTests
{
    [Fact]
    public async Task ABoulderInFlight_IsEvictedByWallScopedEvictions()
    {
        var store = new WallCacheStore();
        var wallId = Guid.NewGuid();
        var boulderId = Guid.NewGuid();
        var cursor = new FeedCursor(Guid.NewGuid(), new FeedPosition(10, 5));
        var gate = new TaskCompletionSource<CacheLoad<Boulder?>>();
        var pending = store.Boulders.GetOrLoad(boulderId, () => gate.Task);

        var evicted = store.EvictBoulders(wallId, cursor, _ => true);
        Assert.Empty(evicted.Boulders); // reported only once its load shows its wall
        gate.SetResult(new CacheLoad<Boulder?>(new Boulder { Id = boulderId, WallId = wallId, Name = "x" }, new FeedCursor(cursor.Epoch, new FeedPosition(9, 1))));
        await pending;
        Assert.Equal(boulderId, await Assert.Single(evicted.InFlight));
        Assert.False(store.Boulders.TryPeek(boulderId, out _)); // the stale result is handed out once, never cached

        var again = new TaskCompletionSource<CacheLoad<Boulder?>>();
        _ = store.Boulders.GetOrLoad(boulderId, () => again.Task);
        Assert.True(store.EvictWall(wallId)); // the own-write path too
        Assert.False(store.Boulders.Keys.Any());
    }

    [Fact]
    public async Task ABoulderInFlightOfAnotherWall_IsNeverReported()
    {
        var store = new WallCacheStore();
        var cursor = new FeedCursor(Guid.NewGuid(), new FeedPosition(10, 5));
        var gate = new TaskCompletionSource<CacheLoad<Boulder?>>();
        var boulderId = Guid.NewGuid();
        _ = store.Boulders.GetOrLoad(boulderId, () => gate.Task);

        var evicted = store.EvictBoulders(Guid.NewGuid(), cursor, _ => true);
        gate.SetResult(new CacheLoad<Boulder?>(new Boulder { Id = boulderId, WallId = Guid.NewGuid(), Name = "x" }, null));

        Assert.Null(await Assert.Single(evicted.InFlight));
    }

    [Fact]
    public async Task ABoulderInFlight_IsReportedToThePage_OnlyForItsOwnWall()
    {
        var gate = new TaskCompletionSource();
        await using var f = new CircuitCacheFixture();
        await f.StartAsync();
        var boulder = await f.AddBoulderAsync("Mine", f.Holds[0]);
        await f.Cache.EnsureFeedAsync(f.WallId); // subscribed, nothing wall-level cached
        var loading = f.Store.Boulders.GetOrLoad(boulder.Id, async () =>
        {
            await gate.Task;
            return new CacheLoad<Boulder?>(boulder, null);
        });

        await f.DeliverAsync(f.Row(FeedKinds.WallUpdated, f.WallId, new WallPayload(0, false)));
        Assert.DoesNotContain(f.Changes, c => c.Scope == WallCacheChangeScope.Boulder);
        gate.SetResult();
        await loading;

        await CircuitCacheFixture.WaitUntilAsync(() => f.Changes.Any(c => c.Scope == WallCacheChangeScope.Boulder && c.BoulderId == boulder.Id));
    }

    [Fact]
    public async Task AFailedLoad_NeverDropsANewerEntry()
    {
        var cache = new FeedTaggedCache<Guid, string>();
        var key = Guid.NewGuid();
        var failing = new TaskCompletionSource<CacheLoad<string>>();
        var first = cache.GetOrLoad(key, () => failing.Task);
        cache.Evict(key);
        await cache.GetOrLoad(key, () => Task.FromResult(new CacheLoad<string>("newer", null)));

        failing.SetException(new InvalidOperationException("db down"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);

        Assert.True(cache.TryPeek(key, out var load));
        Assert.Equal("newer", load.Value);
    }

    [Fact]
    public async Task DisposingDuringASubscribe_LeavesNoHubSubscription()
    {
        GatedWallFeedHub? gated = null;
        await using var f = new CircuitCacheFixture(wrapHub: h => gated = new GatedWallFeedHub(h));
        await f.StartAsync();
        gated!.Close();
        var ensuring = f.Link.EnsureAsync(f.WallId);
        await CircuitCacheFixture.WaitUntilAsync(() => gated.Waiting == 1);

        f.Link.Dispose();
        gated.Open();

        Assert.Null(await ensuring);
        await CircuitCacheFixture.WaitUntilAsync(() => f.Hub.TrackedWallCount == 0);
    }

    [Fact]
    public async Task DisposingDuringAResubscribe_LeavesNoHubSubscription()
    {
        GatedWallFeedHub? gated = null;
        await using var f = new CircuitCacheFixture(wrapHub: h => gated = new GatedWallFeedHub(h));
        await f.StartAsync();
        await f.Cache.GetWallAsync(f.WallId);
        gated!.Close();
        f.Fake.Viewer = FeedViewer.Member(f.Harness.Owner.Id);
        await f.DeliverAsync(f.Row(FeedKinds.MemberChanged, f.Harness.Owner.Id, audience: FeedAudience.Members));
        await CircuitCacheFixture.WaitUntilAsync(() => gated.Waiting == 1);

        f.Link.Dispose();
        gated.Open();

        await CircuitCacheFixture.WaitUntilAsync(() => f.Hub.TrackedWallCount == 0);
    }

    [Fact]
    public async Task ABatchIsCutAtARevoke_AndTheSlotDropsEverythingAfterIt()
    {
        await using var f = await StartedAsync();
        var me = f.Harness.Owner.Id;
        var slot = (await f.Link.EnsureAsync(f.WallId))!;
        f.Fake.Viewer = FeedViewer.Member(me);

        await f.DeliverAsync(
            f.Row(FeedKinds.AccessRevoked, f.WallId, new MemberPayload("Member"), FeedAudience.User, me),
            f.Row("setters-only", audience: FeedAudience.Setters));

        Assert.Equal([FeedKinds.AccessRevoked], f.Deliveries[^1].Events.Select(e => e.Kind));
        Assert.True(slot.IsRevoked);
        var count = f.Deliveries.Count;
        slot.OnFeed(new FeedDelivery(f.WallId, f.Deliveries[^1].Events, slot.Start!.Value, Resync: false));
        Assert.Equal(count, f.Deliveries.Count);
        await CircuitCacheFixture.WaitUntilAsync(() => f.Link.ViewerOf(f.WallId)?.SameAs(FeedViewer.Member(me)) == true);
    }

    [Fact]
    public async Task ARevokeThenAReAddInOneBatch_KeepsTheWall()
    {
        await using var f = await StartedAsync();
        var me = f.Harness.Owner.Id;
        f.Fake.Viewer = FeedViewer.Member(me); // re-added as a plain member

        await f.DeliverAsync(
            f.Row(FeedKinds.AccessRevoked, f.WallId, null, FeedAudience.User, me),
            f.Row(FeedKinds.MemberChanged, me, new MemberPayload("Member"), FeedAudience.Members));
        await CircuitCacheFixture.WaitUntilAsync(() => f.Link.ViewerOf(f.WallId)?.SameAs(FeedViewer.Member(me)) == true);

        Assert.Empty(f.Lost);
        await f.DeliverAsync(f.Row(FeedKinds.WallManifest)); // the fresh slot delivers
    }

    [Fact]
    public async Task AWallUpdateThatKeptTheOwner_RereadsNothing()
    {
        await using var f = await StartedAsync();
        var before = f.Fake.Resolved;

        await f.DeliverAsync(f.Row(FeedKinds.WallUpdated, f.WallId, new WallPayload(0, OwnerChanged: false)));
        await Task.Delay(200);

        Assert.Equal(before, f.Fake.Resolved);
    }

    [Fact]
    public async Task TheWallMapper_FlagsAnOwnerChange_AndOnlyThat()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);
        var heir = await harness.AddMemberAsync("heir@test", WallRole.Admin);
        await using (var db = StructureFeedKit.Context(harness))
        {
            var wall = db.Walls.Single();
            wall.Name = "Renamed";
            await db.SaveChangesAsync();
            wall.OwnerId = heir.Id;
            await db.SaveChangesAsync();
        }

        var flags = (await StructureFeedKit.RowsAsync(harness)).Where(r => r.Kind == FeedKinds.WallUpdated)
            .Select(r => StructureFeedKit.Payload(r).GetProperty("ownerChanged").GetBoolean());
        Assert.Equal([false, true], flags);
    }

    [Fact]
    public async Task AWallTheCircuitNoLongerCaches_IsUnsubscribed_WhenAnotherWallSubscribes()
    {
        await using var f = await StartedAsync();
        var other = await AddWallAsync(f);
        await f.DeliverAsync(f.Row(FeedKinds.WallResync)); // nothing of the first wall is cached any more

        await f.Cache.GetWallAsync(other);

        Assert.Null(f.Link.ViewerOf(f.WallId));
        Assert.NotNull(f.Link.ViewerOf(other));
        Assert.Equal(1, f.Hub.TrackedWallCount);
    }

    [Fact]
    public async Task ANoAccessAnswer_IsKept_UntilTheWallListChanges()
    {
        await using var f = new CircuitCacheFixture();
        await f.StartAsync();
        f.Fake.Viewer = null;
        var before = f.Fake.Resolved;

        Assert.Null(await f.Link.EnsureAsync(f.WallId));
        Assert.Null(await f.Link.EnsureAsync(f.WallId));
        Assert.Equal(before + 1, f.Fake.Resolved);

        var joined = new WallChange
        {
            WallId = f.WallId, Kind = FeedKinds.WallsChanged, EntityId = f.WallId, Audience = FeedAudience.User, AudienceUserId = Guid.NewGuid(),
        };
        f.Commits.Publish([joined]);
        f.Fake.Viewer = FeedViewer.Member(f.Harness.Owner.Id);
        Assert.NotNull(await f.Link.EnsureAsync(f.WallId));
    }

    [Fact]
    public async Task ADemotionInTheFirstBatch_BeforeTheSubscribeReturned_StillResubscribes()
    {
        GatedWallFeedHub? gated = null;
        await using var f = new CircuitCacheFixture(wrapHub: h => gated = new GatedWallFeedHub(h));
        await f.StartAsync();
        var me = f.Harness.Owner.Id;
        gated!.Close();
        var ensuring = f.Link.EnsureAsync(f.WallId);
        await CircuitCacheFixture.WaitUntilAsync(() => gated.Waiting == 1); // subscribed at the hub, not yet returned

        await f.DeliverAsync(f.Row(FeedKinds.AccessRevoked, f.WallId, new MemberPayload("Member"), FeedAudience.User, me));
        gated.Open();
        await ensuring;

        await CircuitCacheFixture.WaitUntilAsync(() => f.Link.ViewerOf(f.WallId)?.SameAs(FeedViewer.Member(me)) == true);
        await f.DeliverAsync(f.Row(FeedKinds.WallManifest)); // the replacement delivers
    }

    [Fact]
    public async Task TheWallOnScreen_IsSubscribedAndPinned_AndARemovalReachesIt()
    {
        await using var f = new CircuitCacheFixture();
        await f.StartAsync();
        var other = await AddWallAsync(f);

        await f.Cache.WatchRouteAsync($"walls/{f.WallId}/3d"); // a page that reads nothing through the cache
        Assert.NotNull(f.Link.ViewerOf(f.WallId));
        await f.Cache.GetWallAsync(other);
        Assert.NotNull(f.Link.ViewerOf(f.WallId)); // pinned: the cross-wall load did not release it

        await f.DeliverAsync(f.Row(FeedKinds.AccessRevoked, f.WallId, null, FeedAudience.User, f.Harness.Owner.Id));
        await CircuitCacheFixture.WaitUntilAsync(() => f.Lost.Contains(f.WallId));

        await f.Cache.WatchRouteAsync("walls"); // unpinned
        await f.Cache.GetWallAsync(other);
        Assert.Null(f.Link.ViewerOf(f.WallId));
    }

    [Fact]
    public async Task EnsureNeverHandsOutARetiredSlot()
    {
        await using var f = new CircuitCacheFixture();
        await f.StartAsync();
        var first = (await f.Link.EnsureAsync(f.WallId))!;
        first.Retire(); // as Release does, racing this circuit's next EnsureAsync

        var second = await f.Link.EnsureAsync(f.WallId);

        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.False(second.IsRetired);
        await f.DeliverAsync(f.Row(FeedKinds.WallManifest));
    }

    [Theory]
    [InlineData("walls/{0}", true)]
    [InlineData("walls/{0}/boulders/5d9c8a43-40a8-4a8e-9c83-0c6c1b4ab0a1", true)]
    [InlineData("walls/{0}/update?step=2", true)]
    [InlineData("walls/{0}/shared/tok", false)]
    [InlineData("walls/{0}/boulders/5d9c8a43-40a8-4a8e-9c83-0c6c1b4ab0a1/shared/tok", false)]
    [InlineData("walls/5d9c8a43-40a8-4a8e-9c83-0c6c1b4ab0a1", false)]
    [InlineData("walls", false)]
    [InlineData("settings/{0}", false)]
    public void TheRoutesLeftOnLostAccess_AreTheWallsMemberPages(string template, bool expected)
    {
        var wallId = Guid.NewGuid();

        Assert.Equal(expected, WallRoutes.IsMemberPageOf(string.Format(template, wallId), wallId));
    }

    private static async Task<CircuitCacheFixture> StartedAsync()
    {
        var f = new CircuitCacheFixture();
        await f.StartAsync();
        await f.Cache.GetWallAsync(f.WallId);
        return f;
    }

    private static async Task<Guid> AddWallAsync(CircuitCacheFixture f)
    {
        await using var db = f.Harness.CreateContext();
        var wall = new Wall { Name = "Other", OwnerId = f.Harness.Owner.Id, Photo = [1], PhotoContentType = "image/jpeg" };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = f.Harness.Owner.Id, Role = WallRole.Admin });
        await db.SaveChangesAsync();
        return wall.Id;
    }
}
