// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Web.State.Live;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A live leaf list on a circuit over the real hub (PR 1.5): hydrated comments patch in, a delete drops, the stats and 3D
/// kinds reach their scopes, a changed viewer reloads, an audience the viewer lacks never arrives, a held list waits for
/// its release, and an event the list's load already reflects is skipped.
/// </summary>
public sealed class LeafFeedTests : IAsyncLifetime
{
    private readonly CircuitCacheFixture f = new();
    private readonly List<LeafBatch> applied = [];
    private Boulder boulder = null!;

    public async Task InitializeAsync()
    {
        await f.StartAsync();
        boulder = await f.AddBoulderAsync("First", f.Holds[0]);
    }

    public async Task DisposeAsync() => await f.DisposeAsync();

    [Fact]
    public async Task ACommentAdded_ArrivesHydrated_AndPatchesTheList_AndItsDeleteDropsIt()
    {
        var comment = await AddCommentAsync("Nice heel hook");
        var list = new CommentList();
        using var feed = Open(CommentList.Kinds);

        await DeliverAsync(Row(FeedKinds.CommentAdded, comment.Id, actor: f.Harness.Owner.Id));
        var added = Assert.Single(Batches().SelectMany(b => b.Events));
        Assert.Equal("Nice heel hook", added.Hydrated?.Text);
        Assert.Equal("Owner", added.Actor?.Name);
        Assert.False(list.Apply(Batches()[0]));
        Assert.Equal(comment.Id, Assert.Single(list.Rows).Id);

        await DeliverAsync(Row(FeedKinds.CommentDeleted, comment.Id));
        Assert.False(list.Apply(Batches()[1]));
        Assert.Empty(list.Rows);
    }

    [Fact]
    public async Task ADraftsSetterRow_NeverReachesAMember()
    {
        f.Fake.Viewer = FeedViewer.Member(f.Harness.Owner.Id);
        using var feed = Open(CommentList.Kinds);

        await f.CommitAsync(true, Row(FeedKinds.CommentAdded, Guid.NewGuid(), audience: FeedAudience.Setters));
        var visible = Row(FeedKinds.CommentAdded, Guid.NewGuid());
        await DeliverAsync(visible);

        Assert.Equal([visible.EntityId], Batches().SelectMany(b => b.Events).Select(e => e.EntityId));
    }

    [Fact]
    public async Task AResubscribeAsAnotherViewer_ReloadsTheList()
    {
        using var feed = Open(CommentList.Kinds);
        await CircuitCacheFixture.WaitUntilAsync(() => f.Link.ViewerOf(f.WallId) is not null);

        await f.CommitAsync(
            true,
            f.Row(FeedKinds.AccessRevoked, f.WallId, new MemberPayload(nameof(WallRole.Member)), FeedAudience.User, user: f.Harness.Owner.Id));

        await CircuitCacheFixture.WaitUntilAsync(() => Batches().Any(b => b.Reload));
    }

    [Fact]
    public async Task AHeldList_GetsNothing_UntilReleased_ThenEverythingAtOnce()
    {
        var held = true;
        using var feed = Open(CommentList.Kinds, () => held);

        await f.DeliverAsync(Row(FeedKinds.CommentAdded, Guid.NewGuid()));
        await f.DeliverAsync(Row(FeedKinds.CommentDeleted, Guid.NewGuid()));
        await Task.Delay(100);
        Assert.Empty(Batches());

        held = false;
        await feed.ReleaseAsync();

        var batch = Assert.Single(Batches());
        Assert.Equal([FeedKinds.CommentAdded, FeedKinds.CommentDeleted], batch.Events.Select(e => e.Kind));
    }

    [Fact]
    public async Task AnEventTheLoadReflects_IsSkipped()
    {
        using var feed = Open(CommentList.Kinds);
        var early = Row(FeedKinds.CommentAdded, Guid.NewGuid());
        await f.CommitAsync(false, early);
        await feed.LoadAsync(() => Task.FromResult(0));

        var later = Row(FeedKinds.CommentAdded, Guid.NewGuid());
        await DeliverAsync(later);

        Assert.Equal([later.EntityId], Batches().SelectMany(b => b.Events).Select(e => e.EntityId));
    }

    [Fact]
    public async Task StatsAndOwnRows_ReachTheirBoulder_NotAnother()
    {
        using var feed = Open(new HashSet<string> { FeedKinds.BoulderStats, FeedKinds.TickMine });

        var other = Row(FeedKinds.BoulderStats, Guid.NewGuid());
        other.BoulderId = Guid.NewGuid();
        await f.CommitAsync(true, other);
        await DeliverAsync(Row(FeedKinds.BoulderStats, boulder.Id), Row(FeedKinds.TickMine, boulder.Id, audience: FeedAudience.User, user: f.Harness.Owner.Id));

        Assert.Equal([FeedKinds.BoulderStats, FeedKinds.TickMine], Batches().SelectMany(b => b.Events).Select(e => e.Kind));
    }

    [Fact]
    public async Task AModelChange_ReachesTheWallsAndTheBouldersThreeDViews()
    {
        var kinds = new HashSet<string> { FeedKinds.View3DChanged };
        using var wallLevel = Open(kinds, wallLevel: true);
        using var boulderLevel = Open(kinds);

        var row = f.Row(FeedKinds.View3DChanged);
        await f.CommitAsync(true, row);

        await CircuitCacheFixture.WaitUntilAsync(() => Batches().Count == 2);
        Assert.All(Batches(), b => Assert.Equal(FeedKinds.View3DChanged, Assert.Single(b.Events).Kind));
    }

    [Fact]
    public async Task AResync_ReloadsTheList_AndADisposedListHearsNothing()
    {
        var feed = Open(CommentList.Kinds);
        await DeliverAsync(f.Row(FeedKinds.WallResync));
        Assert.True(Assert.Single(Batches()).Reload);

        feed.Dispose();
        await f.DeliverAsync(Row(FeedKinds.CommentAdded, Guid.NewGuid()));
        await Task.Delay(100);
        Assert.Single(Batches());
    }

    private LeafFeed Open(IReadOnlySet<string> kinds, Func<bool>? held = null, bool wallLevel = false) =>
        LeafFeed.Open(f.Cache, new LeafScope(f.WallId, wallLevel ? null : boulder.Id, kinds), run => run(), Record, held);

    private Task Record(LeafBatch batch)
    {
        lock (applied)
        {
            applied.Add(batch);
        }

        return Task.CompletedTask;
    }

    private List<LeafBatch> Batches()
    {
        lock (applied)
        {
            return applied.ToList();
        }
    }

    // Commits, wakes and waits until the list applied one more batch.
    private async Task DeliverAsync(params WallChange[] rows)
    {
        var before = Batches().Count;
        await f.CommitAsync(true, rows);
        await CircuitCacheFixture.WaitUntilAsync(() => Batches().Count > before);
    }

    private WallChange Row(string kind, Guid entity, FeedAudience audience = FeedAudience.Wall, Guid? user = null, Guid? actor = null)
    {
        var row = f.Row(kind, entity, kind.StartsWith("comment.", StringComparison.Ordinal) ? new CommentPayload(boulder.Id) : null, audience, user);
        row.BoulderId = boulder.Id;
        row.ActorUserId = actor;
        return row;
    }

    private async Task<BoulderComment> AddCommentAsync(string text)
    {
        await using var db = f.Harness.CreateContext();
        var comment = new BoulderComment { BoulderId = boulder.Id, UserId = f.Harness.Owner.Id, Text = text };
        db.BoulderComments.Add(comment);
        await db.SaveChangesAsync();
        return comment;
    }
}
