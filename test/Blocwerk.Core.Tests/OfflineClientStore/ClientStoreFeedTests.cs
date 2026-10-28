// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;

namespace Blocwerk.Core.Tests.OfflineClientStore;

/// <summary>
/// Feed events on the store (the seam the sync loop, PR 3.12, applies its stream through): comments by id, a boulder's
/// deletion and an outsider's purge on a move to draft, the follow-ups (index, marks, resync) per kind, own ratings
/// ignored, and nothing applied under another label.
/// </summary>
public sealed class ClientStoreFeedTests
{
    [Fact]
    public async Task CommentAdded_JoinsOncePerId_AndCommentDeleted_RemovesIt()
    {
        var (store, _) = await LoadedAsync(Device(UserA));
        var id = Guid.NewGuid();
        var added = Envelope(FeedKinds.CommentAdded, id, Boulder1, hydrated: new("fresh", At.AddMinutes(1)));

        Assert.Equal(FeedFollowUp.RefetchIndex, store.Apply(UserA, added));
        store.Apply(UserA, added);
        Assert.Equal(["nice", "fresh"], (await store.GetWallAsync(Wall))!.CommentsFor(Boulder1).Select(c => c.Text));

        Assert.Equal(FeedFollowUp.RefetchIndex, store.Apply(UserA, Envelope(FeedKinds.CommentDeleted, id, Boulder1)));
        Assert.Equal(["nice"], (await store.GetWallAsync(Wall))!.CommentsFor(Boulder1).Select(c => c.Text));
    }

    [Fact]
    public async Task ACommentWithoutItsContent_WaitsForTheIndex()
    {
        var (store, wall) = await LoadedAsync(Device(UserA));

        store.Apply(UserA, Envelope(FeedKinds.CommentAdded, Guid.NewGuid(), Boulder1));
        store.Apply(UserA, Envelope(FeedKinds.CommentAdded, Guid.NewGuid(), Boulder1, hydrated: new("x", At)) with { Tombstone = true });

        Assert.Same(wall, await store.GetWallAsync(Wall));
    }

    [Fact]
    public async Task BoulderDeleted_DropsTheBoulderAndItsComments_ForEveryClass()
    {
        var (store, _) = await LoadedAsync(Device(UserA));

        var followUp = store.Apply(UserA, Envelope(FeedKinds.BoulderDeleted, Boulder1, Boulder1));

        var wall = (await store.GetWallAsync(Wall))!;
        Assert.Equal(FeedFollowUp.RefetchIndex | FeedFollowUp.RefetchMine, followUp);
        Assert.DoesNotContain(wall.Comments, c => c.BoulderId == Boulder1);
        Assert.False(wall.HasBoulder(Boulder1));
    }

    [Theory]
    [InlineData(Anon, true)]
    [InlineData(Kiosk, false)]
    [InlineData(UserA, true)]
    public async Task ABoulderGoneToDraft_PurgesItsCommentsAtOnce_ForTheOutsiderClasses(string label, bool shared)
    {
        var (store, _) = await LoadedAsync(Device(label, shareWall: shared ? Wall : null, member: false));

        var followUp = store.Apply(label, Envelope(FeedKinds.BoulderChanged, Boulder1, Boulder1, new BoulderPayload(2, true)));

        var wall = (await store.GetWallAsync(Wall))!;
        Assert.True(followUp.HasFlag(FeedFollowUp.RefetchIndex));
        Assert.DoesNotContain(wall.Comments, c => c.BoulderId == Boulder1);
        Assert.False(wall.HasBoulder(Boulder1));
        Assert.Single(wall.CommentsFor(Boulder2));
    }

    [Fact]
    public async Task AMembersDevice_KeepsADraftsComments_AndAPublishedChangeKeepsThemForAll()
    {
        var (member, memberWall) = await LoadedAsync(Device(UserA));
        member.Apply(UserA, Envelope(FeedKinds.BoulderChanged, Boulder1, Boulder1, new BoulderPayload(2, true)));
        Assert.Same(memberWall, await member.GetWallAsync(Wall));

        var (outsider, outsiderWall) = await LoadedAsync(Device(Anon, shareWall: Wall));
        outsider.Apply(Anon, Envelope(FeedKinds.BoulderChanged, Boulder1, Boulder1, new BoulderPayload(2, false)));
        Assert.Same(outsiderWall, await outsider.GetWallAsync(Wall));
    }

    [Fact]
    public async Task ARestampedBoulder_CountsOnlyTheRowOfItsCachedGenerationOrANewerOne()
    {
        var (store, wall) = await LoadedAsync(Device(Anon, shareWall: Wall));

        // BoulderFeedMapper's second row of a restamp: the OLD generation with the old draft flag (a draft just published).
        store.Apply(Anon, Envelope(FeedKinds.BoulderChanged, Boulder1, Boulder1, new BoulderPayload(0, true)));
        Assert.Same(wall, await store.GetWallAsync(Wall));

        store.Apply(Anon, Envelope(FeedKinds.BoulderChanged, Boulder1, Boulder1, new { generation = "x", isDraft = true }));
        Assert.Same(wall, await store.GetWallAsync(Wall));

        store.Apply(Anon, Envelope(FeedKinds.BoulderChanged, Boulder1, Boulder1, new BoulderPayload(1, true)));
        Assert.False((await store.GetWallAsync(Wall))!.HasBoulder(Boulder1));
    }

    [Theory]
    [InlineData(FeedKinds.TickMine, FeedFollowUp.RefetchMine)]
    [InlineData(FeedKinds.FavoriteChanged, FeedFollowUp.RefetchMine)]
    [InlineData(FeedKinds.RatingMine, FeedFollowUp.None)]
    [InlineData(FeedKinds.TicksResync, FeedFollowUp.Resync | FeedFollowUp.RefetchMine)]
    [InlineData(FeedKinds.UserResync, FeedFollowUp.Resync | FeedFollowUp.RefetchMine)]
    [InlineData(FeedKinds.WallResync, FeedFollowUp.Resync | FeedFollowUp.RefetchMine)]
    [InlineData(FeedStreamEvents.Resync, FeedFollowUp.Resync | FeedFollowUp.RefetchMine)]
    [InlineData(FeedKinds.HoldChanged, FeedFollowUp.RefetchIndex)]
    [InlineData("future.kind", FeedFollowUp.None)]
    public async Task EachKind_HasItsFollowUp_ForASignedInDevice(string kind, FeedFollowUp expected)
    {
        var (store, _) = await LoadedAsync(Device(UserA));

        Assert.Equal(expected, store.Apply(UserA, Envelope(kind)));
    }

    [Fact]
    public async Task AnOutsideDevice_NeverRefetchesMarks()
    {
        var (store, _) = await LoadedAsync(Device(Anon, shareWall: Wall));

        Assert.Equal(FeedFollowUp.None, store.Apply(Anon, Envelope(FeedKinds.TickMine)));
        Assert.Equal(FeedFollowUp.Resync, store.Apply(Anon, Envelope(FeedKinds.UserResync)));
    }

    [Fact]
    public async Task AnUnknownEnvelopeVersion_Resyncs_AndAnotherLabelsEvent_IsNotApplied()
    {
        var (store, wall) = await LoadedAsync(Device(UserA));

        Assert.Equal(FeedFollowUp.Resync | FeedFollowUp.RefetchMine, store.Apply(UserA, Envelope(FeedKinds.CommentAdded) with { V = 99 }));
        Assert.Equal(FeedFollowUp.Resync, store.Apply(UserB, Envelope(FeedKinds.CommentAdded, Guid.NewGuid(), Boulder1, hydrated: new("x", At))));
        Assert.Same(wall, await store.GetWallAsync(Wall));
    }
}
