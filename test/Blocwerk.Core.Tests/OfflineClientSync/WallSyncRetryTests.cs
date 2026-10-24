// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Client.Sync;
using Blocwerk.Contracts.Assets;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientSync.SyncWorld;

namespace Blocwerk.Core.Tests.OfflineClientSync;

/// <summary>
/// A closed stream's probe against a fake server (PR 3.12): a 429 waits its <c>Retry-After</c> plus 0–20 %, no network
/// backs off 5/30/120 s ±20 % and <c>online</c> cuts that to a random 0–10 s, the connection bubbles, and the refusals
/// of the identity matrix (403/404 drop the wall, a 401 hands its reason on so <c>expired</c> locks and any other
/// reason wipes, 410 wipes), each of which stops the loop.
/// </summary>
public sealed class WallSyncRetryTests
{
    [Fact]
    public async Task A429_WaitsItsRetryAfterPlusJitter_ThenProbesAgainAndReopens()
    {
        var world = await SyncingAsync();
        world.Clock.Jitter = 0.5;
        world.Transport.Calls.Clear();
        world.Transport.Changes.Enqueue(new SyncResponse(429, RetryAfter: 30));

        await world.Transport.Stream.ErrorAsync();
        Assert.Equal([TimeSpan.FromSeconds(33)], world.Clock.Running);
        Assert.Equal(["changes 1.2.3"], world.Transport.Calls);

        world.Clock.Fire();
        await world.Settled();
        Assert.Equal(["changes 1.2.3", "changes 1.2.3", "open 1.2.3"], world.Transport.Calls);
        Assert.Empty(world.Transport.Bubbles);
    }

    [Fact]
    public async Task A429WithoutRetryAfter_WaitsTheLimitersFiveSeconds()
    {
        var world = await SyncingAsync();
        world.Clock.Jitter = 0;
        world.Transport.Changes.Enqueue(new SyncResponse(429));

        await world.Transport.Stream.ErrorAsync();

        Assert.Equal([TimeSpan.FromSeconds(5)], world.Clock.Running);
    }

    [Fact]
    public async Task NoNetwork_BacksOffFiveThirtyAndAHundredTwentySeconds_WithJitter_AndShowsOfflineOnce()
    {
        var world = await SyncingAsync();
        world.Clock.Jitter = 1;
        for (var i = 0; i < 4; i++)
        {
            world.Transport.Changes.Enqueue(new SyncResponse(0));
        }

        await world.Transport.Stream.ErrorAsync();
        for (var i = 0; i < 3; i++)
        {
            world.Clock.Fire();
        }

        Assert.Equal([5 * 1.2, 30 * 1.2, 120 * 1.2, 120 * 1.2], world.Clock.Asked.Select(d => Math.Round(d.TotalSeconds, 6)));
        Assert.Equal(["offline"], world.Transport.Bubbles);

        world.Clock.Fire();
        await world.Settled();
        Assert.Equal(["offline", "online"], world.Transport.Bubbles);
        Assert.True(world.Sync.Live);
    }

    [Fact]
    public async Task ComingBackOnline_CutsTheBackoffShort_ToARandomZeroToTenSeconds()
    {
        var world = await SyncingAsync();
        world.Clock.Jitter = 0.25;
        world.Transport.Changes.Enqueue(new SyncResponse(503));
        world.Transport.Changes.Enqueue(new SyncResponse(0));
        await world.Transport.Stream.ErrorAsync();
        world.Clock.Fire();
        Assert.Equal([TimeSpan.FromSeconds(30 * 0.9)], world.Clock.Running);
        world.Transport.Calls.Clear();

        await world.Transport.Signals!.OnOnlineAsync();

        Assert.Equal([TimeSpan.FromSeconds(2.5)], world.Clock.Running);
        Assert.DoesNotContain(world.Transport.Calls, c => c.StartsWith("changes", StringComparison.Ordinal));
        world.Clock.Fire();
        await world.Settled();
        Assert.Contains("changes 1.2.3", world.Transport.Calls);
        Assert.True(world.Sync.Live);
    }

    [Fact]
    public async Task OnlineWhileLive_ChangesNothingForTheStream()
    {
        var world = await SyncingAsync();
        var stream = world.Transport.Stream;

        await world.Transport.Signals!.OnOnlineAsync();

        Assert.False(stream.Disposed);
        Assert.Empty(world.Clock.Running);
    }

    [Theory]
    [InlineData(403, null)]
    [InlineData(404, null)]
    public async Task A403Or404_DropsTheWall_AndStopsItsLoop(int status, string? reason)
    {
        var world = await SyncingAsync();
        world.Transport.Changes.Enqueue(new SyncResponse(status, reason));

        await world.Transport.Stream.ErrorAsync();
        await world.Settled();

        Assert.Equal((UserA, Wall, status, reason, false), Assert.Single(world.Transport.Refusals));
        Assert.Null(world.Sync.WallId);
        Assert.Single(world.Transport.Streams);
    }

    [Theory]
    [InlineData(401, SignOutReasons.Expired)]
    [InlineData(401, SignOutReasons.Forced)]
    [InlineData(401, SignOutReasons.Gone)]
    [InlineData(401, null)]
    [InlineData(410, null)]
    public async Task ASessionRefusal_HandsItsReasonToTheMatrix_AndTheStoreReReadsWhatIsLeft(int status, string? reason)
    {
        var world = await SyncingAsync();
        world.Transport.Changes.Enqueue(new SyncResponse(status, reason));

        // The device side of the matrix: an expired session locks, any other reason or a 410 wipes.
        world.Transport.OnRefused = (_, why) => world.Source.Snapshot = StoreSnapshot.Nothing(status == 401 && why == SignOutReasons.Expired ? "locked" : "empty");

        await world.Transport.Stream.ErrorAsync();
        await world.Settled();

        Assert.Equal((UserA, Wall, status, reason, false), Assert.Single(world.Transport.Refusals));
        Assert.Equal(status == 401 && reason == SignOutReasons.Expired ? StoreState.Locked : StoreState.Empty, world.Store.State);
        Assert.Null(world.Sync.WallId);
        Assert.Single(world.Transport.Streams);
    }

    [Theory]
    [InlineData(401, SignOutReasons.Expired)]
    [InlineData(403, null)]
    [InlineData(410, null)]
    public async Task ARefusedIndex_GoesToTheMatrix_AndNoStreamOpens(int status, string? reason)
    {
        var world = await DeviceAsync(UserA);
        world.Transport.Indexes.Enqueue(new SyncIndex(status, status == 403 ? "dropped" : "signed-out", reason));

        await world.Sync.StartAsync(WallPage);
        await world.Settled();

        Assert.Equal((UserA, Wall, status, reason, false), Assert.Single(world.Transport.Refusals));
        Assert.Empty(world.Transport.Streams);
    }

    [Fact]
    public async Task ARefusedMarksFetch_DropsOnlyTheMarks_AndTheWallStaysLive()
    {
        var world = await DeviceAsync(UserA);
        world.Transport.Mines.Enqueue(new SyncResponse(403));

        await world.Sync.StartAsync(WallPage);
        await world.Settled();

        Assert.Equal((UserA, Wall, 403, null, true), Assert.Single(world.Transport.Refusals));
        Assert.True(world.Sync.Live);
    }

    [Fact]
    public async Task AWallWhoseLedgerRowWent_StopsWithoutARefusal()
    {
        var world = await DeviceAsync(UserA);
        world.Transport.Indexes.Enqueue(new SyncIndex(0, "skipped"));

        await world.Sync.StartAsync(WallPage);
        await world.Settled();

        Assert.Empty(world.Transport.Refusals);
        Assert.Empty(world.Transport.Streams);
    }
}
