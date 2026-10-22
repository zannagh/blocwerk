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
    private readonly ExclusiveDispatcher dispatcher = new();
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
        Assert.DoesNotContain(Batches(), b => b.Structural); // only the viewer changed: a 3D view keeps its stage
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
        await dispatcher.Run(feed.ReleaseAsync);

        var batch = Assert.Single(Batches());
        Assert.Equal([FeedKinds.CommentAdded, FeedKinds.CommentDeleted], batch.Events.Select(e => e.Kind));
    }

    [Fact]
    public async Task AnEventTheLoadReflects_IsSkipped()
    {
        using var feed = Open(CommentList.Kinds);
        await CaughtUpAsync();
        var early = Row(FeedKinds.CommentAdded, Guid.NewGuid());
        await f.CommitAsync(false, early);
        await dispatcher.Run(() => feed.LoadAsync(() => Task.FromResult(0), _ => { }));

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

    [Fact]
    public async Task AnEventDuringAnInFlightLoad_IsHeld_AndAppliedAfterIt()
    {
        using var feed = Open(CommentList.Kinds);
        var during = Row(FeedKinds.CommentAdded, Guid.NewGuid());
        var committed = new List<int>();

        await dispatcher.Run(() => feed.LoadAsync(
            async () =>
            {
                // Committed after the load's cursor: the query may or may not see it, so it must not be lost.
                var before = f.Deliveries.Count;
                await f.CommitAsync(true, during);
                await CircuitCacheFixture.WaitUntilAsync(() => f.Deliveries.Count > before);
                await Task.Delay(50);
                Assert.Empty(Batches()); // held while the load runs
                return 1;
            },
            committed.Add));

        Assert.Equal([1], committed);
        await CircuitCacheFixture.WaitUntilAsync(() => Batches().Count == 1);
        Assert.Equal([during.EntityId], Assert.Single(Batches()).Events.Select(e => e.EntityId));
    }

    [Fact]
    public async Task OfOverlappingLoads_OnlyTheLatestCommits()
    {
        using var feed = Open(CommentList.Kinds);
        var slow = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var landed = new List<string>();

        var first = dispatcher.Run(() => feed.LoadAsync(() => slow.Task, landed.Add));
        var second = await dispatcher.Run(() => feed.LoadAsync(() => Task.FromResult("new"), landed.Add));
        slow.SetResult("old");

        Assert.True(second);
        Assert.False(await first);
        Assert.Equal(["new"], landed);
    }

    [Fact]
    public async Task ResyncsWithinTheDelay_ReloadOnce()
    {
        using var feed = Open(CommentList.Kinds);
        await f.CommitAsync(true, f.Row(FeedKinds.WallResync));
        await f.CommitAsync(true, f.Row(FeedKinds.WallResync));
        await CircuitCacheFixture.WaitUntilAsync(() => Batches().Count == 1);
        await Task.Delay(LeafFeed.ReloadDelay * 2);

        var batch = Assert.Single(Batches());
        Assert.True(batch.Reload);
        Assert.True(batch.Structural); // a wall resync: the content may really have changed
    }

    [Fact]
    public async Task HeldBatches_AreAppliedAfterTheLoadReturned_NeverInsideIt()
    {
        var order = new List<string>();
        LeafFeed? feed = null;
        feed = LeafFeed.Open(f.Cache, new LeafScope(f.WallId, boulder.Id, CommentList.Kinds), dispatcher.Run, async batch =>
        {
            // The list re-reads on what was held: a nested load, as a component's apply would run.
            await feed!.LoadAsync(() => Task.FromResult("inner"), v => order.Add(v));
        });
        using var opened = feed;

        await dispatcher.Run(async () =>
        {
            await feed.LoadAsync(
                async () =>
                {
                    var before = f.Deliveries.Count;
                    await f.CommitAsync(true, Row(FeedKinds.CommentAdded, Guid.NewGuid()));
                    await CircuitCacheFixture.WaitUntilAsync(() => f.Deliveries.Count > before);
                    await Task.Delay(50);
                    return "outer";
                },
                v => order.Add(v));
            order.Add("returned"); // what a component assigns after the load: the held batch must not overtake it
        });

        await CircuitCacheFixture.WaitUntilAsync(() => order.Count == 3);
        Assert.Equal(["outer", "returned", "inner"], order);
    }

    [Fact]
    public async Task AHoldChange_RebuildsA3DView_Structurally()
    {
        var batches = new List<LeafBatch>();
        using var feed = LeafFeed.Open(f.Cache, Web.State.Live.Wall3DViewIdentity.Scope(f.WallId), dispatcher.Run, b =>
        {
            lock (batches)
            {
                batches.Add(b);
            }

            return Task.CompletedTask;
        });

        await f.CommitAsync(true, f.Row(FeedKinds.HoldChanged, f.Holds[0].Id, new HoldPayload(0, null)));
        await CircuitCacheFixture.WaitUntilAsync(() =>
        {
            lock (batches)
            {
                return batches.Count == 1;
            }
        });

        lock (batches)
        {
            Assert.True(batches[0].Reload && batches[0].Structural);
        }
    }

    [Fact]
    public async Task AWatchedWall_KeepsItsSubscription_WhenAnotherWallSubscribes()
    {
        using var feed = Open(CommentList.Kinds);
        await dispatcher.Run(() => feed.LoadAsync(() => Task.FromResult(0), _ => { }));
        Assert.NotNull(f.Link.ViewerOf(f.WallId));

        await f.Cache.GetWallAsync(Guid.NewGuid()); // subscribes another wall and releases idle ones

        Assert.NotNull(f.Link.ViewerOf(f.WallId));
    }

    private LeafFeed Open(IReadOnlySet<string> kinds, Func<bool>? held = null, bool wallLevel = false) =>
        LeafFeed.Open(f.Cache, new LeafScope(f.WallId, wallLevel ? null : boulder.Id, kinds), dispatcher.Run, Record, held);

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

    // Waits until the circuit's subscription is open and the hub has read past a row of another boulder (which the list
    // ignores). Without it the hub's first read after the subscribe can land after a row the test commits next and deliver
    // it as live, before any load: a list with no load yet applies it, which is right, but not what a test about the
    // load's cursor means to check.
    private async Task CaughtUpAsync()
    {
        await f.Cache.EnsureFeedAsync(f.WallId);
        var elsewhere = Row(FeedKinds.CommentAdded, Guid.NewGuid());
        elsewhere.BoulderId = Guid.NewGuid();
        await f.DeliverAsync(elsewhere);
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
