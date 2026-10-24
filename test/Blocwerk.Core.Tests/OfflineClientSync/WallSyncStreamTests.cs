// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Client.Sync;
using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientSync.SyncWorld;

namespace Blocwerk.Core.Tests.OfflineClientSync;

/// <summary>
/// The open wall's stream against a fake one (PR 3.12): the replay from the documents' oldest cursor, comments added
/// and deleted by id, each event applied once, the resyncs, a bye, the debounced index refetch, and the tab hiding.
/// </summary>
public sealed class WallSyncStreamTests
{
    private static readonly Guid Fresh = Guid.Parse("c3333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task Start_ReloadsTheIndexAndMarks_ThenReplaysTheStreamFromTheDocumentsOldestCursor()
    {
        var world = await DeviceAsync(UserA);
        world.Transport.IndexDocs[DataDocumentKinds.Wall] = new(Sha('a'), "1.1.9");

        await world.Sync.StartAsync(WallPage);
        await world.Settled();

        Assert.Equal([$"index {Wall} ", $"mine {Wall}", "open 1.1.9"], world.Transport.Calls.Take(3));
        Assert.True(world.Sync.Live);
        Assert.Null(world.Transport.Stream.Token);
    }

    [Fact]
    public async Task ADocumentWithANewSha_ReplacesItsKindWhole_AndItsCursorJoinsTheReplay()
    {
        var world = await DeviceAsync(UserA);
        world.Transport.Indexes.Enqueue(new SyncIndex(200, "updated", null, new Dictionary<string, StoreDocRef>
        {
            [DataDocumentKinds.Wall] = new(Sha('a'), Built),
            [DataDocumentKinds.Boulders] = new(Sha('b'), Built),
            [DataDocumentKinds.Comments] = new(Sha('d'), "1.2.7"),
        }));
        world.Transport.Documents[Sha('d')] = CommentsJson(Comment(CommentA, Boulder1, "rewritten"));

        await world.Sync.StartAsync(WallPage);
        await world.Settled();

        Assert.Equal(["doc comments dddd"], world.Transport.Of("doc"));
        Assert.Equal(["rewritten"], await world.CommentTextsAsync(Boulder1));
        Assert.Empty(await world.CommentTextsAsync(Boulder2));
        Assert.Equal("open 1.2.3", world.Transport.Of("open").Single());
    }

    [Fact]
    public async Task ADocumentThatDidNotArrive_OpensTheStreamWithoutACursor_SoTheServerResyncs()
    {
        var world = await DeviceAsync(UserA);
        world.Transport.Indexes.Enqueue(new SyncIndex(200, "updated", null, new Dictionary<string, StoreDocRef>
        {
            [DataDocumentKinds.Comments] = new(Sha('d'), "1.2.7"),
        }));

        await world.Sync.StartAsync(WallPage);
        await world.Settled();

        Assert.Null(world.Transport.Stream.After);
    }

    [Fact]
    public async Task CommentAddedAndDeleted_ApplyByIdAtOnce_AndOneIndexRefetchFollowsTheBurstAfterTenSeconds()
    {
        var world = await SyncingAsync();
        world.Transport.Calls.Clear();

        await world.Transport.Stream.SendAsync(At("1.2.4", FeedKinds.CommentAdded, Fresh, Boulder1, "send it"));
        await world.Settled();
        Assert.Equal(["nice", "send it"], await world.CommentTextsAsync(Boulder1));

        await world.Transport.Stream.SendAsync(At("1.2.5", FeedKinds.CommentDeleted, CommentA, Boulder1));
        await world.Settled();
        Assert.Equal(["send it"], await world.CommentTextsAsync(Boulder1));

        Assert.Equal([SyncDebounce.IndexDelay], world.Clock.Running);
        Assert.Empty(world.Transport.Of("index"));
        world.Clock.Fire(SyncDebounce.IndexDelay);
        await world.Settled();
        Assert.Single(world.Transport.Of("index"));
        Assert.Equal("1.2.5", world.Sync.Cursor);
    }

    [Fact]
    public async Task NoEventIsAppliedTwice_AReplayedAddNeverBringsBackADeletedComment()
    {
        var world = await SyncingAsync();
        var stream = world.Transport.Stream;
        await stream.SendAsync(At("1.2.4", FeedKinds.CommentAdded, Fresh, Boulder1, "send it"));
        await stream.SendAsync(At("1.2.5", FeedKinds.CommentDeleted, Fresh, Boulder1));
        await world.Settled();

        // A reconnect's replay and the hub that already tracked the wall can both deliver the same rows.
        await stream.SendAsync(At("1.2.4", FeedKinds.CommentAdded, Fresh, Boulder1, "send it"));
        world.Transport.Changes.Enqueue(FakeSyncTransport.Page("1.2.5", events: At("1.2.4", FeedKinds.CommentAdded, Fresh, Boulder1, "send it")));
        await stream.ErrorAsync();
        await world.Settled();

        Assert.Equal(["nice"], await world.CommentTextsAsync(Boulder1));
        Assert.Equal("1.2.5", world.Sync.Cursor);
    }

    [Fact]
    public async Task AResyncEvent_ReloadsIndexAndMarks_ThenContinuesAfterTheServersMark()
    {
        var world = await SyncingAsync();
        world.Transport.Calls.Clear();

        await world.Transport.Stream.ResyncAsync("1.9.0");
        await world.Settled();

        Assert.Equal([$"index {Wall} ", $"mine {Wall}"], world.Transport.Calls);
        Assert.Equal("1.9.0", world.Sync.Cursor);
        Assert.True(world.Sync.Live);
    }

    [Theory]
    [InlineData(FeedKinds.WallResync)]
    [InlineData(FeedKinds.TicksResync)]
    [InlineData(FeedKinds.UserResync)]
    public async Task TheResyncKinds_ReloadEverything_MarksIncluded(string kind)
    {
        var world = await SyncingAsync();
        world.Transport.Calls.Clear();

        await world.Transport.Stream.SendAsync(At("1.2.4", kind));
        await world.Settled();

        Assert.Equal([$"index {Wall} ", $"mine {Wall}"], world.Transport.Calls);
    }

    [Fact]
    public async Task TheMineKinds_RefetchTheMarks_AndOwnRatingsAreIgnored()
    {
        var world = await SyncingAsync();
        world.Transport.Calls.Clear();
        world.Transport.Mines.Enqueue(Mine(UserA, Boulder1));

        await world.Transport.Stream.SendAsync(At("1.2.4", FeedKinds.FavoriteChanged, Boulder1, Boulder1));
        await world.Transport.Stream.SendAsync(At("1.2.5", FeedKinds.RatingMine, Boulder1, Boulder1));
        await world.Settled();
        Assert.Empty(world.Transport.Calls);
        world.Clock.Fire(SyncDebounce.MineDelay);
        await world.Settled();

        Assert.Equal([$"mine {Wall}"], world.Transport.Calls);
        Assert.True((await world.WallAsync()).Mine.IsFavourite(Boulder1));
        Assert.Empty(world.Clock.Running);
    }

    [Fact]
    public async Task AnUnknownPayloadVersion_ReloadsTheWall()
    {
        var world = await SyncingAsync();
        world.Transport.Calls.Clear();

        await world.Transport.Stream.SendAsync(Envelope(FeedKinds.HoldChanged, data: new { sv = 99 }) with { Cursor = "1.2.4" });
        await world.Settled();

        Assert.Equal([$"index {Wall} ", $"mine {Wall}"], world.Transport.Calls);
    }

    [Theory]
    [InlineData(FeedStreamEvents.ByeLifetime)]
    [InlineData(FeedStreamEvents.ByeOverflow)]
    [InlineData(FeedStreamEvents.ByeAccess)]
    public async Task ABye_ClosesTheStream_CatchesUpByPagesFromTheCursor_ThenReopensAfterTheLastPage(string reason)
    {
        var world = await SyncingAsync();
        var first = world.Transport.Stream;
        world.Transport.Calls.Clear();
        world.Transport.Changes.Enqueue(FakeSyncTransport.Page("1.2.4", more: true, events: At("1.2.4", FeedKinds.CommentAdded, Fresh, Boulder1, "one")));
        world.Transport.Changes.Enqueue(FakeSyncTransport.Page("1.3.0"));

        await first.ByeAsync(reason);
        Assert.Equal([SyncDelays.Bye(0.5)], world.Clock.Running);
        Assert.Empty(world.Transport.Of("changes"));
        world.Clock.Fire();
        await world.Settled();

        Assert.True(first.Disposed);
        Assert.Equal(["changes 1.2.3", "changes 1.2.4", "open 1.3.0"], world.Transport.Calls.Where(c => !c.StartsWith("index", StringComparison.Ordinal)));
        Assert.Equal(["nice", "one"], await world.CommentTextsAsync(Boulder1));
        Assert.True(world.Sync.Live);
    }

    [Fact]
    public async Task ACatchUpPageThatResyncs_ReloadsAndReopensAfterItsMark()
    {
        var world = await SyncingAsync();
        world.Transport.Calls.Clear();
        world.Transport.Changes.Enqueue(FakeSyncTransport.Page("1.8.0", resync: true));

        await world.Transport.Stream.ErrorAsync();
        await world.Settled();

        Assert.Equal(["changes 1.2.3", $"index {Wall} ", $"mine {Wall}", "open 1.8.0"], world.Transport.Calls);
    }

    [Fact]
    public async Task HidingTheTab_ClosesTheStreamAndEveryWait_AndShowingItStartsOver()
    {
        var world = await SyncingAsync();
        var stream = world.Transport.Stream;
        await stream.SendAsync(At("1.2.4", FeedKinds.HoldChanged));
        await world.Settled();
        Assert.Single(world.Clock.Running);

        await world.Transport.Signals!.OnVisibilityAsync(false);
        await stream.SendAsync(At("1.2.5", FeedKinds.CommentAdded, Fresh, Boulder1, "late"));
        await world.Settled();

        Assert.True(stream.Disposed);
        Assert.False(world.Sync.Live);
        Assert.Empty(world.Clock.Running);
        Assert.Equal(["nice"], await world.CommentTextsAsync(Boulder1));

        world.Transport.Calls.Clear();
        await world.Transport.Signals.OnVisibilityAsync(true);
        await world.Settled();
        Assert.Equal([$"index {Wall} ", $"mine {Wall}", "open 1.2.3", "me 1.0.0"], world.Transport.Calls);
    }
}
