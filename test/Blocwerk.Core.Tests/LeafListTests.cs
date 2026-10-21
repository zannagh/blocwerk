// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Helpers;
using Blocwerk.Web.State.Live;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The live leaf lists' pure parts (PR 1.5): what a delivery selects for one list, what a batch plans, and the comment
/// list's patching by id (insert in order, replace, drop on delete or tombstone, re-read when the event cannot say enough).
/// </summary>
public sealed class LeafListTests
{
    private static readonly Guid Epoch = Guid.NewGuid();
    private static readonly Guid Wall = Guid.NewGuid();
    private static readonly Guid Boulder = Guid.NewGuid();
    private static readonly LeafScope Comments = new(Wall, Boulder, CommentList.Kinds);

    [Fact]
    public void ADelivery_SelectsItsBouldersKinds_AndWallWideRows()
    {
        var mine = Env(FeedKinds.CommentAdded, seq: 1);
        var other = Env(FeedKinds.CommentAdded, seq: 2) with { BoulderId = Guid.NewGuid() };
        var wallLevelActivity = Env(FeedKinds.ActivityAdded, seq: 3) with { BoulderId = null };
        var profile = Env(FeedKinds.UserProfile, seq: 4) with { BoulderId = null };
        var stats = Env(FeedKinds.BoulderStats, seq: 5);

        var batch = Comments.Select(Delivery(mine, other, wallLevelActivity, profile, stats), loadedAt: null);

        Assert.NotNull(batch);
        Assert.False(batch.Reload);
        Assert.Equal([mine, profile], batch.Events);
        Assert.Null(Comments.Select(Delivery(other) with { WallId = Guid.NewGuid() }, null));
    }

    [Fact]
    public void EventsTheLoadAlreadyHas_AreSkipped()
    {
        var at = Cursor(10);

        var batch = Comments.Select(Delivery(Env(FeedKinds.CommentAdded, seq: 9), Env(FeedKinds.CommentAdded, seq: 10), Env(FeedKinds.CommentAdded, seq: 11)), at);

        Assert.Equal(["11"], batch!.Events.Select(e => e.Cursor.Split('.')[2]));
        Assert.Null(Comments.Select(Delivery(Env(FeedKinds.CommentDeleted, seq: 4)), at));
        Assert.NotNull(Comments.Select(Delivery(Env(FeedKinds.CommentDeleted, seq: 4) with { Cursor = new FeedCursor(Guid.NewGuid(), new FeedPosition(null, 4)).ToString() }), at));
    }

    [Fact]
    public void Resyncs_UnknownSchemas_AndBadCursors_Reload()
    {
        Assert.True(Comments.Select(Delivery() with { Resync = true }, null)!.Reload);
        Assert.True(Comments.Select(Delivery(Env(FeedKinds.WallResync, seq: 1) with { BoulderId = null }), null)!.Reload);
        var future = JsonDocument.Parse("""{"sv":99,"boulderId":"00000000-0000-0000-0000-000000000000"}""").RootElement;
        Assert.True(Comments.Select(Delivery(Env(FeedKinds.CommentAdded, seq: 1) with { Data = future }), null)!.Reload);
        Assert.True(Comments.Select(Delivery(Env(FeedKinds.CommentAdded, seq: 1) with { Cursor = "nonsense" }), null)!.Reload);
    }

    [Fact]
    public void APlan_KeepsTheLastEventPerId_AndReloadsOnKindsItCannotPatch()
    {
        var id = Guid.NewGuid();
        var added = Env(FeedKinds.VideoAdded, seq: 1, entity: id);
        var deleted = Env(FeedKinds.VideoDeleted, seq: 2, entity: id);

        var plan = LeafPlan.Of(new LeafBatch([added, deleted], false), new HashSet<string>(), new HashSet<string> { FeedKinds.VideoDeleted });

        Assert.Equal([id], plan.Removals);
        Assert.True(plan.Reload); // video.added carries no tile: re-read
        var onlyDelete = LeafPlan.Of(new LeafBatch([deleted], false), new HashSet<string>(), new HashSet<string> { FeedKinds.VideoDeleted });
        Assert.False(onlyDelete.Reload);
    }

    [Fact]
    public void AHydratedComment_IsInsertedNewestFirst_AndReapplyingItChangesNothing()
    {
        var list = Loaded(("old", -10), ("older", -20));
        var batch = new LeafBatch([Added("new", 0), Added("middle", -15)], false);

        Assert.False(list.Apply(batch));
        Assert.False(list.Apply(batch));

        Assert.Equal(["new", "old", "middle", "older"], list.Rows.Select(r => r.Text));
        Assert.Equal(4, list.Total);
    }

    [Fact]
    public void ADeleteOrATombstone_DropsTheRow()
    {
        var list = Loaded(("a", -1), ("b", -2));
        var a = list.Rows[0].Id;
        var b = list.Rows[1].Id;

        Assert.False(list.Apply(new LeafBatch([Env(FeedKinds.CommentDeleted, seq: 1, entity: a)], false)));
        Assert.False(list.Apply(new LeafBatch([Env(FeedKinds.CommentAdded, seq: 2, entity: b) with { Tombstone = true }], false)));

        Assert.Empty(list.Rows);
        Assert.Equal(0, list.Total);
    }

    [Fact]
    public void ACommentOlderThanAPartialWindow_OnlyCounts()
    {
        var list = new CommentList();
        list.Load([Comment("shown", -1)], total: 5, viewer: null);

        Assert.False(list.Apply(new LeafBatch([Added("way back", -100)], false)));

        Assert.Single(list.Rows);
        Assert.Equal(6, list.Total);
    }

    [Fact]
    public void AProfileChange_ReloadsOnlyWhenItNamesSomeoneShown()
    {
        var list = Loaded(("a", -1));
        var author = list.Rows[0].OwnerId!.Value;

        Assert.False(list.Apply(new LeafBatch([Env(FeedKinds.UserProfile, seq: 1, entity: Guid.NewGuid()) with { BoulderId = null }], false)));
        Assert.True(list.Apply(new LeafBatch([Env(FeedKinds.UserProfile, seq: 2, entity: author) with { BoulderId = null }], false)));
        Assert.True(list.Apply(LeafBatch.ReloadOnly));
    }

    [Fact]
    public void LoadedBylines_FollowAuthorVisibility_LikeTheFeed()
    {
        var erased = new User { Identifier = "gone", DisplayName = "Gone", DeletedAt = DateTimeOffset.UtcNow };
        var plain = new User { Identifier = "p", DisplayName = "Plain" };

        Assert.Equal(PlaceholderIdentity.DisplayName, LeafAuthors.Describe(erased, FeedViewer.Member(Guid.NewGuid())).Name);
        Assert.Equal(GhostUser.Id, LeafAuthors.Describe(erased, FeedViewer.Member(Guid.NewGuid())).Id);
        Assert.Null(LeafAuthors.Describe(plain, null).Id); // a share viewer gets no id without an avatar
        Assert.Equal(plain.Id, LeafAuthors.Describe(plain, FeedViewer.Member(Guid.NewGuid())).Id);
    }

    [Fact]
    public void AHeldBatch_MergesInOrder_AndKeepsAReload()
    {
        var first = new LeafBatch([Env(FeedKinds.CommentAdded, seq: 1)], false);
        var merged = first.Merge(LeafBatch.ReloadOnly).Merge(new LeafBatch([Env(FeedKinds.CommentDeleted, seq: 2)], false));

        Assert.True(merged.Reload);
        Assert.Equal([FeedKinds.CommentAdded, FeedKinds.CommentDeleted], merged.Events.Select(e => e.Kind));
    }

    private static CommentList Loaded(params (string Text, int Minutes)[] comments)
    {
        var list = new CommentList();
        list.Load(comments.Select(c => Comment(c.Text, c.Minutes)), comments.Length, FeedViewer.Member(Guid.NewGuid()));
        return list;
    }

    private static BoulderComment Comment(string text, int minutes)
    {
        var user = new User { Identifier = text, DisplayName = text };
        return new BoulderComment { Text = text, User = user, UserId = user.Id, BoulderId = Boulder, CreatedAt = Now.AddMinutes(minutes) };
    }

    private static DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;

    private static FeedEnvelope Added(string text, int minutes) =>
        Env(FeedKinds.CommentAdded, seq: 100, entity: Guid.NewGuid()) with
        {
            Actor = new FeedActor(Guid.NewGuid(), "Sam", false, null),
            Hydrated = new FeedContent(text, Now.AddMinutes(minutes)),
        };

    private static FeedEnvelope Env(string kind, long seq, Guid? entity = null) => new()
    {
        Cursor = Cursor(seq).ToString(),
        WallId = Wall,
        BoulderId = Boulder,
        Kind = kind,
        EntityId = entity ?? Guid.NewGuid(),
    };

    private static FeedCursor Cursor(long seq) => new(Epoch, new FeedPosition(null, seq));

    private static FeedDelivery Delivery(params FeedEnvelope[] events) => new(Wall, events, Cursor(1000), false);
}
