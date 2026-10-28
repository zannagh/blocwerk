// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Contracts.Mine;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;

namespace Blocwerk.Core.Tests.OfflineClientStore;

/// <summary>
/// The kiosk's offline mode on the C# side (Phase 3 PR 3.15, <see cref="KioskStoreGate"/>): a tablet's <c>k-</c>
/// snapshot shows its one bound wall in the kiosk class and never marks; a snapshot naming another wall beside it shows
/// nothing at all ("Not available offline"); a wall the snapshot does not name is never read, whatever the URL asks.
/// </summary>
public sealed class ClientStoreKioskTests
{
    private static readonly Guid OtherWall = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task AKioskTablet_ShowsItsBoundWall_InTheKioskClass_WithNoMarks()
    {
        var flash = new MineDocumentTick(Boulder1, 1, Sent: false, Flashed: true, At);
        var (store, wall) = await LoadedAsync(Device(Kiosk, mine: [Mine(flash)]));

        Assert.Equal(StoreState.Ready, store.State);
        Assert.Equal(Kiosk, store.Partition);
        Assert.Equal(StoreAudience.Kiosk, wall.Audience);
        Assert.Equal(0, wall.Mine.Count);
        Assert.False(wall.Mine.IsSent(Boulder1));
    }

    [Fact]
    public async Task AKioskSnapshotWithASecondWall_ShowsNothing_NotEvenTheFirst()
    {
        var source = Device(Kiosk);
        var bound = Assert.Single(source.Snapshot.Walls);
        source.Snapshot = source.Snapshot with { Walls = [bound, bound with { WallId = OtherWall }] };
        source.Docs[(Kiosk, OtherWall)] = source.Docs[(Kiosk, Wall)] with { WallId = OtherWall };
        var store = new ClientStore(source);

        await store.StartAsync();

        Assert.Equal(StoreState.Kiosk, store.State);
        Assert.Null(store.Partition);
        Assert.Empty(store.Walls);
        Assert.Null(await store.GetWallAsync(Wall));
        Assert.Null(await store.GetWallAsync(OtherWall));
        Assert.Equal(0, source.DocsReads);
    }

    [Fact]
    public async Task AWallTheKioskSnapshotDoesNotName_IsNeverRead()
    {
        var source = Device(Kiosk);
        source.Docs[(Kiosk, OtherWall)] = source.Docs[(Kiosk, Wall)] with { WallId = OtherWall };
        var (store, _) = await LoadedAsync(source);
        var reads = source.DocsReads;

        Assert.Null(await store.GetWallAsync(OtherWall));
        Assert.Equal(reads, source.DocsReads);
    }

    [Theory]
    [InlineData("kiosk")]
    [InlineData("ready")]
    public async Task AKioskSnapshotWithNoWall_IsNotAvailableOffline(string state)
    {
        var source = new FakeStoreSource { Snapshot = new StoreSnapshot(state, Kiosk, null, [], []) };
        var store = new ClientStore(source);

        await store.StartAsync();

        Assert.Equal(StoreState.Kiosk, store.State);
        Assert.Empty(store.Walls);
    }

    [Theory]
    [InlineData(Kiosk, true)]
    [InlineData(UserA, false)]
    [InlineData(Anon, false)]
    [InlineData("none", false)]
    [InlineData(null, false)]
    public void IsKiosk_IsTheKioskLabelOnly(string? partition, bool expected)
    {
        Assert.Equal(expected, KioskStoreGate.IsKiosk(partition));
    }
}
