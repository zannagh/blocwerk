// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;

namespace Blocwerk.Core.Tests.OfflineClientStore;

/// <summary>
/// How <see cref="ClientStore"/> takes the device's changes (PR 3.10 review): refreshes never overlap and only the
/// newest read lands; a read that changes nothing raises nothing; a first read a refresh overtook is retried; a loaded
/// wall stands while its ledger row's key does; an event of a cached wall not opened yet only asks for its follow-up.
/// </summary>
public sealed class ClientStoreRefreshTests
{
    [Fact]
    public async Task AnOlderSnapshot_NeverLandsAfterANewerOne()
    {
        var source = Device(UserA);
        var store = new ClientStore(source);
        await store.StartAsync();
        var gate = new TaskCompletionSource();
        source.SnapshotGate = gate;

        // Read just before another tab locked the partition, and slow to answer.
        var older = store.RefreshAsync();
        source.Snapshot = source.Snapshot with { State = "locked" };
        var newer = store.RefreshAsync();
        gate.SetResult();
        await Task.WhenAll(older, newer);

        Assert.Equal(StoreState.Locked, store.State);
        Assert.Empty(store.Walls);
        Assert.Null(await store.GetWallAsync(Wall));
    }

    [Fact]
    public async Task ARefreshStartedMidClear_NeverLandsTheCacheTheClearRemoved()
    {
        var source = Device(UserA);
        var (store, _) = await LoadedAsync(source);
        var gate = new TaskCompletionSource();
        source.SnapshotGate = gate;
        source.DuringClear = () => _ = store.RefreshAsync();

        var clearing = store.ClearAsync();
        gate.SetResult();
        Assert.True(await clearing);

        Assert.Equal(StoreState.Empty, store.State);
        Assert.Empty(store.Walls);
    }

    [Fact]
    public async Task AnUnchangedRead_ChangesNothing_AndRaisesNothing()
    {
        var source = Device(UserA);
        var (store, wall) = await LoadedAsync(source);
        var changes = 0;
        store.Changed += () => changes++;

        await store.RefreshAsync();
        Assert.Equal(0, changes);
        Assert.Same(wall, await store.GetWallAsync(Wall));

        source.Snapshot = source.Snapshot with { State = "locked" };
        await store.RefreshAsync();
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task AFirstRead_ARefreshOvertook_IsRetriedAgainstTheNewSnapshot()
    {
        var source = Device(UserA);
        var store = new ClientStore(source);
        await store.StartAsync();
        source.DuringDocs = async () =>
        {
            var newer = source.Docs[(UserA, Wall)].Docs.ToDictionary();
            newer[DataDocumentKinds.Comments] = new(Sha('d'), CommentsJson());
            Put(source, UserA, newer);
            await store.RefreshAsync();
        };

        var wall = await store.GetWallAsync(Wall);

        Assert.NotNull(wall);
        Assert.Equal(Sha('d'), wall.Shas[DataDocumentKinds.Comments]);
        Assert.Equal(2, source.DocsReads);
    }

    [Fact]
    public async Task AFeedDelta_SurvivesARefresh_WhileTheRowsKeyStands_ThoughAKindDidNotParse()
    {
        var source = new FakeStoreSource();
        Put(source, UserA, new()
        {
            [DataDocumentKinds.Boulders] = new(Sha('b'), BouldersJson(Boulder(Boulder1, "One"))),
            [DataDocumentKinds.Comments] = new(Sha('c'), "not json"),
        });
        var (store, _) = await LoadedAsync(source);
        store.Apply(UserA, Envelope(FeedKinds.CommentAdded, CommentA, Boulder1, hydrated: new("live", At)));

        source.Snapshot = source.Snapshot with { ShareWall = Guid.NewGuid() };
        await store.RefreshAsync();

        Assert.Equal("live", Assert.Single((await store.GetWallAsync(Wall))!.CommentsFor(Boulder1)).Text);
        Assert.Equal(1, source.DocsReads);
    }

    [Fact]
    public async Task AnEventOfACachedWallNotOpenedYet_AsksOnlyForItsFollowUp()
    {
        var source = Device(UserA);
        var store = new ClientStore(source);
        await store.StartAsync();

        Assert.Equal(FeedFollowUp.RefetchIndex, store.Apply(UserA, Envelope(FeedKinds.HoldChanged)));
        Assert.Equal(FeedFollowUp.RefetchMine, store.Apply(UserA, Envelope(FeedKinds.TickMine)));
        Assert.Equal(FeedFollowUp.Resync | FeedFollowUp.RefetchMine, store.Apply(UserA, Envelope(FeedKinds.WallResync)));
        Assert.Equal(0, source.DocsReads);
        Assert.Equal(FeedFollowUp.Resync, store.Apply(UserA, Envelope(FeedKinds.HoldChanged) with { WallId = Guid.NewGuid() }));
    }
}
