// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Client.Sync;
using Blocwerk.Contracts.Feed;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientSync.SyncWorld;

namespace Blocwerk.Core.Tests.OfflineClientSync;

/// <summary>
/// The own-rows poll and the wall loop's bookkeeping (PR 3.12 review): an answer for another user is never applied, a
/// hidden tab ends the poll's wait, the poll resumes from its stored cursor, a new label polls at once, a wall another
/// tab dropped stops streaming, a loop that stopped itself is no wall, and a replayed draft row never hides a boulder
/// its newer document shows.
/// </summary>
public sealed class MeChangesPollTests
{
    [Fact]
    public async Task AnAnswerForAnotherUser_IsNeverApplied()
    {
        var world = await DeviceAsync(UserA);
        world.Transport.MyChanges.Enqueue(FakeSyncTransport.Page("1.5.0", events: Envelope(FeedKinds.AccessRevoked, Wall) with { Cursor = "1.5.0" }) with { Partition = UserB });

        await world.Sync.StartAsync(WallPage);
        await world.Settled();

        Assert.Empty(world.Transport.Refusals);
        Assert.True(world.Sync.Live);
        Assert.Empty(world.Transport.MeCursors);
    }

    [Fact]
    public async Task HidingTheTab_EndsThePollsRetryAfterWait()
    {
        var world = await SyncingAsync();
        world.Transport.Calls.Clear();
        world.Transport.MyChanges.Enqueue(new SyncResponse(429, RetryAfter: 5));
        var poll = world.Transport.Signals!.OnOnlineAsync();
        Assert.Single(world.Clock.Running);

        await world.Transport.Signals.OnVisibilityAsync(false);
        await poll;

        Assert.Empty(world.Clock.Running);
        Assert.Equal(["me 1.0.0"], world.Transport.Calls);
    }

    [Fact]
    public async Task ThePollResumesFromItsStoredCursor_SoADeviceWithoutMarksResyncsOnce()
    {
        var transport = new FakeSyncTransport();
        transport.MyChanges.Enqueue(FakeSyncTransport.Page("1.6.0", resync: true));
        await SyncingAsync(transport: transport);
        Assert.Equal("1.6.0", transport.MeCursors[UserA]);

        transport.Calls.Clear();
        await SyncingAsync(transport: transport);

        Assert.Equal(["me 1.6.0"], transport.Of("me"));
        Assert.Equal([$"mine {Wall}"], transport.Of("mine"));
    }

    [Fact]
    public async Task AWallWithoutAMarksRow_DoesNotHoldThePollBack()
    {
        var mine = new StoreMineRow(Guid.NewGuid(), null, [], []);
        var world = await SyncingAsync(mine: [new StoreMineRow(Wall, "1.2.2", [], []), mine]);

        Assert.Equal(["me 1.2.2"], world.Transport.Of("me"));
    }

    [Fact]
    public async Task ANewLabel_PollsItsOwnRowsAtOnce()
    {
        var world = await SyncingAsync();
        world.Transport.Calls.Clear();
        world.Transport.MePartition = UserB;
        world.Source.Docs[(UserB, Wall)] = world.Source.Docs[(UserA, Wall)] with { Partition = UserB };
        world.Source.Snapshot = world.Source.Snapshot with { Partition = UserB, Mine = [] };

        await world.Store.RefreshAsync();
        await world.Settled();

        Assert.Contains("me ", world.Transport.Calls);
        Assert.Equal(UserB, world.Transport.Stream.Token ?? world.Store.Partition);
    }

    [Fact]
    public async Task AWallAnotherTabDropped_StopsStreaming()
    {
        var world = await SyncingAsync();
        var stream = world.Transport.Stream;
        var other = new StoreWallRow(Guid.NewGuid(), new Dictionary<string, StoreDocRef>(), true, "k");

        world.Source.Snapshot = world.Source.Snapshot with { Walls = [other] };
        await world.Store.RefreshAsync();

        Assert.True(stream.Disposed);
        Assert.Null(world.Sync.WallId);
    }

    [Fact]
    public async Task ALoopThatStoppedItself_IsNoWall_AndTheStateSaysSo()
    {
        var world = await SyncingAsync();
        var changes = 0;
        world.Sync.Changed += () => changes++;
        world.Transport.Indexes.Enqueue(new SyncIndex(0, "skipped"));

        await world.Transport.Stream.SendAsync(At("1.2.4", FeedKinds.HoldChanged));
        world.Clock.Fire(SyncDebounce.IndexDelay);
        await world.Settled();

        Assert.Null(world.Sync.WallId);
        Assert.False(world.Sync.Live);
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task AReplayedDraftRow_NeverHidesABoulderItsNewerDocumentShows_ButALaterOneDoes()
    {
        var world = await DeviceAsync(Anon);
        world.Transport.IndexDocs["boulders"] = world.Transport.IndexDocs["boulders"] with { Cursor = "1.2.6" };
        await world.Sync.StartAsync($"{WallPage}/shared/tok");
        await world.Settled();
        Assert.Equal(Built, world.Transport.Stream.After);

        // Unpublished at 1.2.4, republished at 1.2.5, the boulders document built at 1.2.6.
        await world.Transport.Stream.SendAsync(At("1.2.4", FeedKinds.BoulderChanged, Boulder1, Boulder1) with { Data = Payload(true) });
        await world.Transport.Stream.SendAsync(At("1.2.5", FeedKinds.BoulderChanged, Boulder1, Boulder1) with { Data = Payload(false) });
        await world.Settled();
        Assert.True((await world.WallAsync()).HasBoulder(Boulder1));
        Assert.Equal(["nice"], await world.CommentTextsAsync(Boulder1));

        await world.Transport.Stream.SendAsync(At("1.2.7", FeedKinds.BoulderChanged, Boulder1, Boulder1) with { Data = Payload(true) });
        await world.Settled();
        Assert.False((await world.WallAsync()).HasBoulder(Boulder1));
    }

    private static System.Text.Json.JsonElement Payload(bool draft) =>
        System.Text.Json.JsonSerializer.SerializeToElement(new Blocwerk.Contracts.Feed.Payloads.BoulderPayload(1, draft), FeedJson.Options);
}
