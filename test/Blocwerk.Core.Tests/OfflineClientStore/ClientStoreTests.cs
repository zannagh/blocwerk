// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Mine;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;

namespace Blocwerk.Core.Tests.OfflineClientStore;

/// <summary>
/// The offline client's store (Phase 3 PR 3.10, <see cref="ClientStore"/>): only a ready store has walls, every other
/// state (a lock above all) holds none and drops what was read; a wall is read once under its snapshot's label and a
/// label change discards the read; a new document sha replaces the old wholesale; marks exist only under <c>u-</c>,
/// a flash counting as a send.
/// </summary>
public sealed class ClientStoreTests
{
    [Fact]
    public async Task AReadyDevice_ReadsTheServersDocuments_ForItsLabel()
    {
        var (store, wall) = await LoadedAsync(Device(UserA));

        Assert.Equal(StoreState.Ready, store.State);
        Assert.Equal(UserA, store.Partition);
        Assert.Equal("Alpha", wall.Wall!.Name);
        Assert.Equal(["One", "Two"], wall.VisibleBoulders.Select(b => b.Name));
        Assert.Equal("nice", Assert.Single(wall.CommentsFor(Boulder1)).Text);
        Assert.Equal(StoreAudience.Member, wall.Audience);
        Assert.Same(wall, await store.GetWallAsync(Wall));
    }

    [Theory]
    [InlineData("locked", StoreState.Locked)]
    [InlineData("updating", StoreState.Updating)]
    [InlineData("unavailable", StoreState.Unavailable)]
    [InlineData("kiosk", StoreState.Kiosk)]
    [InlineData("empty", StoreState.Empty)]
    [InlineData("something-new", StoreState.Unavailable)]
    public async Task EveryStateButReady_HoldsNothing_AndDropsWhatWasRead(string state, StoreState expected)
    {
        var source = Device(UserA);
        var (store, _) = await LoadedAsync(source);
        var rows = source.Snapshot.Walls;
        source.Snapshot = source.Snapshot with { State = state };

        await source.Listener!();

        Assert.Equal(expected, store.State);
        Assert.Empty(store.Walls);
        Assert.Null(store.Partition);
        Assert.Null(await store.GetWallAsync(Wall));
        source.Snapshot = source.Snapshot with { State = "ready", Walls = rows };
        await store.RefreshAsync();
        Assert.NotNull(await store.GetWallAsync(Wall));
        Assert.Equal(2, source.DocsReads);
    }

    [Fact]
    public async Task ALockedDevice_RendersNothing_EvenWithRowsInItsSnapshot()
    {
        var source = Device(UserA);
        source.Snapshot = source.Snapshot with { State = "locked" };
        var store = new ClientStore(source);
        await store.StartAsync();

        Assert.Equal(StoreState.Locked, store.State);
        Assert.Empty(store.Walls);
        Assert.Null(await store.GetWallAsync(Wall));
        Assert.Equal(0, source.DocsReads);
    }

    [Fact]
    public async Task ARefresh_KeepsALoadedWall_OnlyWhileItsLabelAndShasStand()
    {
        var source = Device(UserA);
        var (store, wall) = await LoadedAsync(source);

        await store.RefreshAsync();
        Assert.Same(wall, await store.GetWallAsync(Wall));

        var newer = source.Docs[(UserA, Wall)].Docs.ToDictionary();
        newer[DataDocumentKinds.Comments] = new(Sha('d'), CommentsJson());
        Put(source, UserA, newer);
        await store.RefreshAsync();
        var reread = await store.GetWallAsync(Wall);
        Assert.NotSame(wall, reread);
        Assert.Empty(reread!.CommentsFor(Boulder1));
        Assert.Equal(2, source.DocsReads);

        Put(source, UserB, newer);
        await store.RefreshAsync();
        Assert.Equal(UserB, store.Partition);
        Assert.NotNull(await store.GetWallAsync(Wall));
        Assert.Equal(3, source.DocsReads);
    }

    [Fact]
    public async Task AReadWhoseLabelChanged_IsDiscarded()
    {
        var source = Device(UserA);
        var store = new ClientStore(source);
        await store.StartAsync();
        source.DuringDocs = async () =>
        {
            source.Snapshot = source.Snapshot with { Partition = UserB };
            await store.RefreshAsync();
        };

        Assert.Null(await store.GetWallAsync(Wall));
        Assert.Equal(UserB, store.Partition);
    }

    [Fact]
    public async Task DocumentsThatAreNoLongerReadable_RefreshTheStore()
    {
        var source = Device(UserA);
        var store = new ClientStore(source);
        await store.StartAsync();
        source.Docs.Clear();
        source.Snapshot = source.Snapshot with { State = "locked" };

        Assert.Null(await store.GetWallAsync(Wall));
        Assert.Equal(StoreState.Locked, store.State);
    }

    [Fact]
    public async Task ANewCommentsSha_ReplacesTheCachedCommentsWholesale_NeverMerged()
    {
        var (store, _) = await LoadedAsync(Device(UserA));
        store.Apply(UserA, Envelope("comment.added", Guid.NewGuid(), Boulder1, hydrated: new("live", At)));
        Assert.Equal(2, (await store.GetWallAsync(Wall))!.CommentsFor(Boulder1).Count);

        var renamed = Comment(CommentB, Boulder2, "hard") with { Author = Alice with { Id = null, Name = "Deleted user" } };
        Assert.True(store.ReplaceDocument(UserA, Wall, DataDocumentKinds.Comments, Sha('e'), CommentsJson(renamed)));

        var wall = (await store.GetWallAsync(Wall))!;
        Assert.Empty(wall.CommentsFor(Boulder1));
        Assert.Equal("Deleted user", Assert.Single(wall.CommentsFor(Boulder2)).Author.Name);
        Assert.Equal(Sha('e'), wall.Shas[DataDocumentKinds.Comments]);
    }

    [Fact]
    public async Task AReplacement_ForAnotherLabel_OrAMalformedDocument_ChangesNothing()
    {
        var (store, wall) = await LoadedAsync(Device(UserA));

        Assert.False(store.ReplaceDocument(UserB, Wall, DataDocumentKinds.Comments, Sha('e'), CommentsJson()));
        Assert.False(store.ReplaceDocument(UserA, Wall, DataDocumentKinds.Comments, Sha('e'), "{\"v\":1,\"wallId\":\"" + Guid.NewGuid() + "\",\"comments\":[]}"));
        Assert.False(store.ReplaceDocument(UserA, Wall, DataDocumentKinds.Comments, Sha('e'), "{\"v\":2,\"wallId\":\"" + Wall + "\",\"comments\":[]}"));
        Assert.False(store.ReplaceDocument(UserA, Wall, DataDocumentKinds.Comments, Sha('e'), "{\"v\":1,\"wallId\":\"" + Wall + "\"}"));
        Assert.False(store.ReplaceDocument(UserA, Wall, DataDocumentKinds.Comments, Sha('e'), "not json"));
        Assert.Same(wall, await store.GetWallAsync(Wall));
    }

    [Fact]
    public async Task CommentsOfABoulderTheBouldersDocumentDoesNotList_AreNeverShown()
    {
        var (store, _) = await LoadedAsync(Device(Anon, shareWall: Wall));

        store.ReplaceDocument(Anon, Wall, DataDocumentKinds.Boulders, Sha('f'), BouldersJson(Boulder(Boulder2, "Two")));

        var wall = (await store.GetWallAsync(Wall))!;
        Assert.Empty(wall.CommentsFor(Boulder1));
        Assert.Equal(2, wall.Comments.Count);
    }

    [Fact]
    public async Task Marks_CountAFlashAsASend_AndExistOnlyUnderAUserLabel()
    {
        var flash = new MineDocumentTick(Boulder1, 1, Sent: false, Flashed: true, At);
        var tried = new MineDocumentTick(Boulder2, 3, Sent: false, Flashed: false, At);
        var (_, mine) = await LoadedAsync(Device(UserA, mine: [Mine(flash, tried)]));

        Assert.True(mine.Mine.IsSent(Boulder1));
        Assert.True(mine.Mine.IsFlashed(Boulder1));
        Assert.False(mine.Mine.IsSent(Boulder2));
        Assert.Equal(3, mine.Mine.TickFor(Boulder2)!.Attempts);
        Assert.True(mine.Mine.IsFavourite(Boulder2));

        foreach (var label in new[] { Anon, Kiosk })
        {
            var (store, wall) = await LoadedAsync(Device(label, shareWall: Wall, mine: [Mine(flash)]));
            Assert.Equal(0, wall.Mine.Count);
            Assert.False(store.ReplaceMine(label, new MineDocument(1, Wall, label, "1", [flash], [])));
        }
    }

    [Fact]
    public async Task ReplacedMarks_MustBeTheStoresOwnLabel()
    {
        var (store, _) = await LoadedAsync(Device(UserA));
        var flash = new MineDocumentTick(Boulder1, 1, false, true, At);

        Assert.False(store.ReplaceMine(UserA, new MineDocument(1, Wall, UserB, "1", [flash], [])));
        Assert.True(store.ReplaceMine(UserA, new MineDocument(1, Wall, UserA, "1", [flash], [])));
        Assert.True((await store.GetWallAsync(Wall))!.Mine.IsSent(Boulder1));
    }

    [Theory]
    [InlineData(UserA, false, StoreAudience.Member)]
    [InlineData(UserA, true, StoreAudience.Outsider)]
    [InlineData(Anon, true, StoreAudience.Outsider)]
    [InlineData(Kiosk, false, StoreAudience.Kiosk)]
    public async Task TheAudience_FollowsTheLabelAndTheShareLink(string label, bool shared, StoreAudience expected)
    {
        var (store, wall) = await LoadedAsync(Device(label, shareWall: shared ? Wall : null));

        Assert.Equal(expected, store.AudienceOf(Wall));
        Assert.Equal(expected, wall.Audience);
    }

    [Fact]
    public async Task Clear_ClearsTheDevice_AndTheStoreShowsNothing()
    {
        var source = Device(UserA);
        var (store, _) = await LoadedAsync(source);
        var changes = 0;
        store.Changed += () => changes++;

        Assert.True(await store.ClearAsync());

        Assert.Equal(1, source.Clears);
        Assert.Equal(StoreState.Empty, store.State);
        Assert.Empty(store.Walls);
        Assert.Null(await store.GetWallAsync(Wall));
        Assert.Equal(1, changes);
    }
}
