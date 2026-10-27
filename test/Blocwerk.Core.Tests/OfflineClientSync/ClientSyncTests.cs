// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Client.Sync;
using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Mine;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientSync.SyncWorld;

namespace Blocwerk.Core.Tests.OfflineClientSync;

/// <summary>
/// Which device syncs what (PR 3.12, plan §3): a kiosk polls nothing, a share link syncs its wall with its token but
/// never polls <c>/api/me</c>, a signed-in device polls its own rows on visibility and <c>online</c> (from its marks'
/// oldest cursor; <c>access.revoked</c> drops the wall, the resync kinds refetch every wall's marks, own ratings are
/// ignored), and a lock or another identity stops the wall's loop.
/// </summary>
public sealed class ClientSyncTests
{
    private static readonly Guid OtherWall = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task AKiosk_PollsNothing_NoIndexNoStreamNoMarksNoMeChanges()
    {
        var world = await DeviceAsync(Kiosk);
        Assert.Equal(Kiosk, world.Store.Partition);

        await world.Sync.StartAsync(WallPage);
        await world.Transport.Signals!.OnVisibilityAsync(true);
        await world.Transport.Signals.OnOnlineAsync();
        await world.Sync.NavigateAsync(WallPage + "/boulders/" + Boulder1);
        await world.Settled();

        Assert.Empty(world.Transport.Calls);
    }

    [Fact]
    public async Task AShareLink_SyncsItsWallWithItsToken_ButNeverPollsMeOrFetchesMarks()
    {
        var world = await SyncingAsync(Anon, page: $"{WallPage}/shared/t%20k");
        await world.Transport.Signals!.OnOnlineAsync();
        await world.Transport.Stream.SendAsync(SyncWorld.At("1.2.4", FeedKinds.TickMine, Boulder1, Boulder1));
        await world.Settled();

        Assert.Equal([$"index {Wall} t k", "open 1.2.3"], world.Transport.Calls);
        Assert.Equal("t k", world.Transport.Stream.Token);
    }

    [Fact]
    public async Task AWallTheDeviceHoldsNothingOf_SyncsNothing()
    {
        var world = await SyncingAsync(page: $"https://blocwerk.test/v/walls/{OtherWall}");

        Assert.Null(world.Sync.WallId);
        Assert.Equal(["me "], world.Transport.Calls);
    }

    [Fact]
    public async Task APageWithoutAWall_SyncsNoWall_AndLeavingTheWallStopsIt()
    {
        var world = await SyncingAsync(page: "https://blocwerk.test/v/");
        Assert.Empty(world.Transport.Of("index"));

        await world.Sync.NavigateAsync(WallPage);
        await world.Settled();
        var stream = world.Transport.Stream;
        await world.Sync.NavigateAsync(WallPage + "/boulders/" + Boulder1);
        Assert.False(stream.Disposed);

        await world.Sync.NavigateAsync("https://blocwerk.test/v/walls");
        Assert.True(stream.Disposed);
        Assert.Null(world.Sync.WallId);
    }

    [Fact]
    public async Task AnotherIdentityOrALock_StopsTheLoop()
    {
        var world = await SyncingAsync();
        var stream = world.Transport.Stream;

        world.Source.Snapshot = StoreSnapshot.Nothing("locked");
        await world.Store.RefreshAsync();

        Assert.True(stream.Disposed);
        Assert.Null(world.Sync.WallId);
    }

    [Fact]
    public async Task TheMePoll_StartsAtTheMarksOldestCursor_AndRunsOnVisibilityAndOnline()
    {
        var world = await SyncingAsync(mine: [new StoreMineRow(Wall, "1.2.1", [], [])]);
        Assert.Equal(["me 1.2.1"], world.Transport.Of("me"));

        world.Transport.MyChanges.Enqueue(FakeSyncTransport.Page("1.4.0"));
        await world.Transport.Signals!.OnOnlineAsync();
        await world.Transport.Signals.OnVisibilityAsync(false);
        await world.Transport.Signals.OnVisibilityAsync(true);
        await world.Settled();

        Assert.Equal(["me 1.2.1", "me 1.2.1", "me 1.4.0"], world.Transport.Of("me"));
    }

    [Fact]
    public async Task AccessRevoked_DropsThatWall_AndStopsItsLoop()
    {
        var world = await DeviceAsync(UserA);
        world.Transport.MyChanges.Enqueue(FakeSyncTransport.Page("1.5.0", events: Own("1.5.0", FeedKinds.AccessRevoked, Wall, entity: Wall)));

        await world.Sync.StartAsync(WallPage);
        await world.Settled();

        Assert.Equal((UserA, Wall, 403, null, false), Assert.Single(world.Transport.Refusals));
        Assert.Null(world.Sync.WallId);
        Assert.True(world.Transport.Stream.Disposed);
    }

    [Theory]
    [InlineData(FeedKinds.TicksResync)]
    [InlineData(FeedKinds.UserResync)]
    public async Task TheResyncKinds_RefetchEveryCachedWallsMarks(string kind)
    {
        var world = await SyncingAsync();
        world.Transport.Calls.Clear();
        world.Transport.MyChanges.Enqueue(FakeSyncTransport.Page("1.5.0", events: Own("1.5.0", kind, null)));
        world.Transport.Mines.Enqueue(Mine(UserA, Boulder2));

        await world.Transport.Signals!.OnOnlineAsync();

        Assert.Equal(["me 1.0.0", $"mine {Wall}"], world.Transport.Calls);
        Assert.True((await world.WallAsync()).Mine.IsFavourite(Boulder2));
    }

    [Fact]
    public async Task OwnRatingsAndUnknownKinds_AreIgnored_AndAMarkRefetchesOnlyItsCachedWall()
    {
        var world = await SyncingAsync();
        world.Transport.Calls.Clear();
        world.Transport.MyChanges.Enqueue(FakeSyncTransport.Page(
            "1.5.0",
            events: [Own("1.4.0", FeedKinds.RatingMine, Wall), Own("1.4.1", "future.kind", Wall), Own("1.4.2", FeedKinds.TickMine, OtherWall), Own("1.4.3", FeedKinds.TickMine, Wall)]));

        await world.Transport.Signals!.OnOnlineAsync();

        Assert.Equal(["me 1.0.0", $"mine {Wall}"], world.Transport.Calls);
        Assert.Empty(world.Transport.Refusals);
    }

    [Fact]
    public async Task AResyncPage_RefetchesEveryWallsMarks_AndContinuesAfterItsMark()
    {
        var world = await SyncingAsync();
        world.Transport.Calls.Clear();
        world.Transport.MyChanges.Enqueue(FakeSyncTransport.Page("1.6.0", resync: true));

        await world.Transport.Signals!.OnOnlineAsync();
        await world.Transport.Signals.OnOnlineAsync();

        Assert.Equal(["me 1.0.0", $"mine {Wall}", "me 1.6.0"], world.Transport.Calls);
    }

    [Theory]
    [InlineData(401, "expired")]
    [InlineData(410, null)]
    public async Task ASessionRefusalOfTheMePoll_GoesToTheMatrix(int status, string? reason)
    {
        var world = await SyncingAsync();
        world.Transport.MyChanges.Enqueue(new SyncResponse(status, reason));

        await world.Transport.Signals!.OnOnlineAsync();

        Assert.Equal((UserA, Guid.Empty, status, reason, false), Assert.Single(world.Transport.Refusals));
        Assert.Null(world.Sync.WallId);
    }

    [Fact]
    public async Task AMePoll429_WaitsItsRetryAfter_ThenAsksOnceMore()
    {
        var world = await SyncingAsync();
        world.Clock.Jitter = 0;
        world.Transport.Calls.Clear();
        world.Transport.MyChanges.Enqueue(new SyncResponse(429, RetryAfter: 5));

        var poll = world.Transport.Signals!.OnOnlineAsync();
        Assert.Equal([TimeSpan.FromSeconds(5)], world.Clock.Running);
        world.Clock.Fire();
        await poll;

        Assert.Equal(["me 1.0.0", "me 1.0.0"], world.Transport.Calls);
    }

    private static FeedEnvelope Own(string cursor, string kind, Guid? wall, Guid? entity = null) =>
        Envelope(kind, entity) with { Cursor = cursor, WallId = wall };
}
