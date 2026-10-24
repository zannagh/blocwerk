// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Client.Sync;
using Blocwerk.Contracts.Feed;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientSync.SyncWorld;

namespace Blocwerk.Core.Tests.OfflineClientSync;

/// <summary>
/// What the loop costs the server (PR 3.12 review): a refused or blocked stream is never reopened in a loop, marks
/// refetches are debounced per wall, a start resumes after the stored cursor instead of replaying from the oldest
/// document, and a backlog past the stream's replay window becomes one resync.
/// </summary>
public sealed class WallSyncLoadTests
{
    [Fact]
    public async Task AStreamThatNeverOpens_WhileChangesAnswers_IsReopenedAtMostTwiceAMinute()
    {
        var world = await DeviceAsync(UserA);
        world.Transport.StreamsOpen = false;
        await world.Sync.StartAsync(WallPage);
        await world.Settled();

        var elapsed = TimeSpan.Zero;
        for (var i = 0; i < 5; i++)
        {
            await world.Transport.Stream.ErrorAsync();
            var waiting = Assert.Single(world.Clock.Running);
            Assert.True(waiting >= TimeSpan.FromSeconds(30), $"reopen {i + 1} waits {waiting}");
            elapsed += waiting;
            world.Clock.Fire();
            await world.Settled();
        }

        Assert.Equal(6, world.Transport.Streams.Count);
        Assert.True(world.Transport.Streams.Count - 1 <= 2 * (elapsed.TotalMinutes + 1), $"{world.Transport.Streams.Count} opens in {elapsed}");
        Assert.Empty(world.Transport.Bubbles);
    }

    [Fact]
    public async Task AQuietStreamThatDropsOnce_ReopensAtOnce_ButTwoSilentClosesInARowWait()
    {
        var world = await SyncingAsync();

        await world.Transport.Stream.ErrorAsync();
        await world.Settled();
        Assert.Equal(2, world.Transport.Streams.Count);
        Assert.Empty(world.Clock.Running);

        await world.Transport.Stream.ErrorAsync();
        Assert.Equal(2, world.Transport.Streams.Count);
        Assert.Equal([SyncDelays.StreamRefused(1, 0.5)], world.Clock.Running);
    }

    [Fact]
    public async Task AStreamThatDeliveredSomething_ReopensAtOnceAfterItsError()
    {
        var world = await SyncingAsync();
        await world.Transport.Stream.SendAsync(At("1.2.4", FeedKinds.RatingMine, Boulder1, Boulder1));
        await world.Transport.Stream.ErrorAsync();
        await world.Settled();
        await world.Transport.Stream.SendAsync(At("1.2.5", FeedKinds.RatingMine, Boulder1, Boulder1));
        await world.Transport.Stream.ErrorAsync();
        await world.Settled();

        Assert.Equal(3, world.Transport.Streams.Count);
        Assert.Empty(world.Clock.Running);
    }

    [Fact]
    public async Task ABurstOfMineEvents_IsOneMarksFetch_TwoSecondsAfterItBegan()
    {
        var world = await SyncingAsync();
        world.Transport.Calls.Clear();

        for (var i = 4; i < 10; i++)
        {
            await world.Transport.Stream.SendAsync(At($"1.2.{i}", FeedKinds.TickMine, Boulder1, Boulder1));
        }

        await world.Settled();
        Assert.Empty(world.Transport.Of("mine"));
        Assert.Equal([SyncDebounce.MineDelay], world.Clock.Running);
        world.Clock.Fire();
        await world.Settled();

        Assert.Equal([$"mine {Wall}"], world.Transport.Of("mine"));
    }

    [Fact]
    public async Task ShowingTheTabAgain_ResumesAfterTheLastCursorTheDeviceHolds_NotTheOldestDocument()
    {
        var world = await SyncingAsync();
        world.Transport.Mines.Enqueue(Mine(UserA));
        await world.Transport.Stream.SendAsync(At("1.2.4", FeedKinds.TickMine, Boulder1, Boulder1));
        await world.Transport.Stream.SendAsync(At("1.2.5", FeedKinds.FavoriteChanged, Boulder1, Boulder1));
        world.Clock.Fire(SyncDebounce.MineDelay);
        await world.Settled();
        Assert.Equal("1.2.5", world.Transport.WallCursors[(UserA, Wall)]);

        // A comment is applied but its document is not refetched yet: the device does not hold 1.2.6.
        await world.Transport.Stream.SendAsync(At("1.2.6", FeedKinds.CommentAdded, Guid.NewGuid(), Boulder1, "late"));
        await world.Settled();
        await world.Transport.Signals!.OnVisibilityAsync(false);
        await world.Transport.Signals.OnVisibilityAsync(true);
        await world.Settled();

        Assert.Equal("1.2.5", world.Transport.Stream.After);
    }

    [Fact]
    public async Task TheIndexRefetch_MovesTheStoredCursorPastTheEventsItCovers()
    {
        var world = await SyncingAsync();
        await world.Transport.Stream.SendAsync(At("1.2.6", FeedKinds.CommentAdded, Guid.NewGuid(), Boulder1, "late"));
        await world.Settled();
        Assert.Equal(Built, world.Transport.WallCursors[(UserA, Wall)]);

        world.Clock.Fire(SyncDebounce.IndexDelay);
        await world.Settled();

        Assert.Equal("1.2.6", world.Transport.WallCursors[(UserA, Wall)]);
    }

    [Fact]
    public async Task AStoredCursorOlderThanTheDocuments_IsNotUsed()
    {
        var transport = new FakeSyncTransport();
        transport.WallCursors[(UserA, Wall)] = "1.1.0";
        var world = await DeviceAsync(UserA, transport: transport);

        await world.Sync.StartAsync(WallPage);
        await world.Settled();

        Assert.Equal(Built, world.Transport.Stream.After);
    }

    [Fact]
    public async Task AStoredCursorOfAnotherEpoch_IsNotUsed()
    {
        var transport = new FakeSyncTransport();
        transport.WallCursors[(UserA, Wall)] = "0.9.9";
        var world = await DeviceAsync(UserA, transport: transport);

        await world.Sync.StartAsync(WallPage);
        await world.Settled();

        Assert.Equal(Built, world.Transport.Stream.After);
    }

    [Fact]
    public async Task MarksThatFailedAtStart_AreRetried_SoTheResumeCursorIsStored()
    {
        var world = await DeviceAsync(UserA);
        world.Transport.Mines.Enqueue(new SyncResponse(503));

        await world.Sync.StartAsync(WallPage);
        await world.Settled();
        Assert.False(world.Transport.WallCursors.ContainsKey((UserA, Wall)));
        Assert.Equal([SyncDebounce.MineDelay], world.Clock.Running);

        world.Clock.Fire();
        await world.Settled();

        Assert.Equal(Built, world.Transport.WallCursors[(UserA, Wall)]);
    }

    [Fact]
    public async Task ABacklogPastTheStreamsReplayWindow_OpensWithoutACursor_SoTheServerResyncs()
    {
        var world = await SyncingAsync();
        for (var i = 1; i <= 6; i++)
        {
            world.Transport.Changes.Enqueue(FakeSyncTransport.Page($"1.3.{i}", more: true));
        }

        await world.Transport.Stream.ErrorAsync();
        await world.Settled();

        Assert.Equal(4, world.Transport.Of("changes").Count);
        Assert.Null(world.Transport.Stream.After);
    }

    [Fact]
    public async Task AResyncWhoseReloadFailed_KeepsTheCursor_AndProbesAgainAfterABackoff()
    {
        var world = await SyncingAsync();
        world.Transport.Indexes.Enqueue(new SyncIndex(0, "offline"));
        var stream = world.Transport.Stream;

        await stream.ResyncAsync("1.9.0");

        Assert.Equal(Built, world.Sync.Cursor);
        Assert.True(stream.Disposed);
        Assert.Equal([SyncDelays.Network(1, 0.5)], world.Clock.Running);
        world.Clock.Fire();
        await world.Settled();
        Assert.Equal("changes 1.2.3", world.Transport.Of("changes").Single());
    }
}
