// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Web.State;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="Web.State.WallCacheState"/> on the hub (PR 1.4): each feed kind evicts only what it touches, a raised
/// change follows only an actual eviction, and kinds that live in no cache here evict nothing.
/// </summary>
public sealed class WallCacheFeedEvictionTests : IAsyncLifetime
{
    private readonly CircuitCacheFixture f = new();
    private Boulder first = null!;
    private Boulder second = null!;

    public async Task InitializeAsync()
    {
        await f.StartAsync(holdCount: 2);
        first = await f.AddBoulderAsync("First", f.Holds[0]);
        second = await f.AddBoulderAsync("Second", f.Holds[1]);
        await f.WarmAsync(first, second);
    }

    public async Task DisposeAsync() => await f.DisposeAsync();

    [Fact]
    public async Task AHoldEvent_EvictsItsGeneration_TheAggregate_AndTheBouldersLinkingIt()
    {
        await f.DeliverAsync(f.Row(FeedKinds.HoldChanged, f.Holds[0].Id, new HoldPayload(0, null)));

        Assert.False(f.Store.Walls.TryPeek(f.WallId, out _));
        Assert.Equal([(f.WallId, 1)], f.Store.HoldSets.Keys);
        Assert.Equal([second.Id], f.Store.Boulders.Keys);
        Assert.Contains(f.Changes, c => c is { Scope: WallCacheChangeScope.Wall } && c.WallId == f.WallId);
    }

    [Fact]
    public async Task AHoldEventOfAnUncachedGeneration_LeavesTheCachedSets()
    {
        await f.DeliverAsync(f.Row(FeedKinds.HoldDeleted, Guid.NewGuid(), new HoldPayload(7, null)));

        Assert.Equal(2, f.Store.HoldSets.Keys.Count());
        Assert.Equal(2, f.Store.Boulders.Keys.Count());
    }

    [Fact]
    public async Task ABoulderEvent_EvictsThatBoulderAndTheAggregate_Only()
    {
        await f.DeliverAsync(f.Row(FeedKinds.BoulderChanged, first.Id, new BoulderPayload(0, false)));

        Assert.False(f.Store.Walls.TryPeek(f.WallId, out _));
        Assert.Equal([second.Id], f.Store.Boulders.Keys);
        Assert.Equal(2, f.Store.HoldSets.Keys.Count());
    }

    [Fact]
    public async Task WallUpdated_EvictsTheAggregateAndItsBoulders_NotTheHoldSets()
    {
        await f.DeliverAsync(f.Row(FeedKinds.WallUpdated, f.WallId, new GenerationPayload(0)));

        Assert.False(f.Store.Walls.TryPeek(f.WallId, out _));
        Assert.Empty(f.Store.Boulders.Keys);
        Assert.Equal(2, f.Store.HoldSets.Keys.Count());
    }

    [Theory]
    [InlineData(FeedKinds.WallResync)]
    [InlineData(FeedKinds.SegmentChanged)]
    [InlineData(FeedKinds.PanelChanged)]
    [InlineData(FeedKinds.LinkChanged)]
    [InlineData(FeedKinds.VolumeChanged)]
    public async Task StructuralKinds_EvictTheWholeWall(string kind)
    {
        await f.DeliverAsync(f.Row(kind));

        AssertWallGone();
    }

    [Theory]
    [InlineData(FeedKinds.MemberChanged)]
    public async Task AMemberChange_EvictsTheAggregateOnly(string kind)
    {
        await f.DeliverAsync(f.Row(kind, audience: FeedAudience.Members));

        Assert.False(f.Store.Walls.TryPeek(f.WallId, out _));
        Assert.Equal(2, f.Store.Boulders.Keys.Count());
        Assert.Equal(2, f.Store.HoldSets.Keys.Count());
    }

    [Fact]
    public async Task AProfileChange_EvictsTheAggregate_AndTheBouldersOfThatUser()
    {
        await f.DeliverAsync(f.Row(FeedKinds.UserProfile, f.Harness.Owner.Id, audience: FeedAudience.Members));

        Assert.False(f.Store.Walls.TryPeek(f.WallId, out _));
        Assert.Empty(f.Store.Boulders.Keys); // the owner created both
        Assert.Equal(2, f.Store.HoldSets.Keys.Count());
    }

    [Theory]
    [InlineData(FeedKinds.BoulderStats)]
    [InlineData(FeedKinds.CommentAdded)]
    [InlineData(FeedKinds.VideoStatus)]
    [InlineData(FeedKinds.GalleryChanged)]
    [InlineData(FeedKinds.View3DChanged)]
    [InlineData(FeedKinds.ActivityAdded)]
    [InlineData(FeedKinds.WallManifest)] // the aggregate holds no manifest data; only the server's manifest cache reads it
    [InlineData("some.future.kind")]
    public async Task KindsInNoCache_EvictNothing_AndRaiseNoChange(string kind)
    {
        await f.DeliverAsync(f.Row(kind, audience: FeedAudience.Members));

        Assert.True(f.Store.Walls.TryPeek(f.WallId, out _));
        Assert.Equal(2, f.Store.Boulders.Keys.Count());
        Assert.Equal(2, f.Store.HoldSets.Keys.Count());
        Assert.Empty(f.Changes);
    }

    [Fact]
    public async Task APayloadOfANewerSchema_EvictsTheWholeWall()
    {
        await f.DeliverAsync(f.Row(FeedKinds.BoulderChanged, first.Id, rawData: """{"sv":99,"generation":0}"""));

        AssertWallGone();
    }

    [Fact]
    public async Task AResyncDelivery_OrOneOfAnotherEpoch_EvictsTheWholeWall()
    {
        var slot = (await f.Link.EnsureAsync(f.WallId))!;
        var start = slot.Start!.Value;

        slot.OnFeed(new FeedDelivery(f.WallId, [], start, Resync: true));
        AssertWallGone();

        await f.WarmAsync(first);
        var otherEpoch = new FeedCursor(Guid.NewGuid(), start.Position);
        slot.OnFeed(new FeedDelivery(f.WallId, [Envelope(FeedKinds.CommentAdded, otherEpoch)], otherEpoch, Resync: false));
        AssertWallGone();
    }

    [Fact]
    public async Task AnEventTheLoadAlreadyReflects_EvictsNothing()
    {
        await f.CommitAsync(false, f.Row(FeedKinds.BoulderChanged, first.Id, new BoulderPayload(0, false)));
        f.Store.Walls.Evict(f.WallId);
        f.Store.Boulders.Evict(first.Id);
        await f.WarmAsync(first); // reloaded after the commit: its CursorAtLoad is at or after the row

        f.Signal.Wake(new FeedWake(f.WallId, null));
        await CircuitCacheFixture.WaitUntilAsync(() => f.Deliveries.Count == 1);

        Assert.True(f.Store.Walls.TryPeek(f.WallId, out _));
        Assert.True(f.Store.Boulders.TryPeek(first.Id, out _));
        Assert.Empty(f.Changes);
    }

    [Fact]
    public async Task ALoadWithNoCursor_IsEvictedByAnyEventOfItsKind()
    {
        f.Store.Boulders.Evict(first.Id);
        await f.Store.Boulders.GetOrLoad(first.Id, () => Task.FromResult(new Web.State.Feed.CacheLoad<Boulder?>(first, null)));

        await f.DeliverAsync(f.Row(FeedKinds.BoulderChanged, first.Id, new BoulderPayload(0, false)));

        Assert.False(f.Store.Boulders.TryPeek(first.Id, out _));
    }

    private static FeedEnvelope Envelope(string kind, FeedCursor cursor) => new()
    {
        Cursor = cursor.ToString(),
        Kind = kind,
        EntityId = Guid.NewGuid(),
    };

    private void AssertWallGone()
    {
        Assert.False(f.Store.Walls.TryPeek(f.WallId, out _));
        Assert.Empty(f.Store.Boulders.Keys);
        Assert.Empty(f.Store.HoldSets.Keys);
    }
}
