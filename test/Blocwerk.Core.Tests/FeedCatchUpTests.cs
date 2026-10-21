// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The catch-up read's cursor rules, paging and hydration (design §5.8): a missing, unparseable or foreign cursor, or one
/// older than retention, resyncs to the high-water mark; a short page's next is capped at that mark; full pages say
/// <c>more</c>; the content is hydrated as the viewer may see it.
/// </summary>
public sealed class FeedCatchUpTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1.2.3")]
    [InlineData("0123456789abcdef0123456789abcdef.x.1")]
    public async Task AMissingOrUnparseableCursor_Resyncs_ToTheHighWaterMark(string? after)
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();

        var page = await f.CatchUp.ReadWallAsync(f.WallId, FeedViewer.Setter(f.Harness.Owner.Id), after, null);

        Assert.True(page.Resync);
        Assert.Empty(page.Events);
        Assert.False(page.More);
        Assert.Equal((await f.Feed.CurrentAsync()).ToString(), page.Next);
    }

    [Fact]
    public async Task ACursorOfAnotherEpoch_Resyncs()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var foreign = new FeedCursor(Guid.NewGuid(), FeedPosition.Start).ToString();

        var page = FeedRouteFixture.Changes(await f.WallChangesAsync("?after=" + foreign));

        Assert.True(page.Resync);
        Assert.True(FeedCursor.TryParse(page.Next, out var next));
        Assert.Equal((await f.Feed.CurrentAsync()).Epoch, next.Epoch);
    }

    /// <summary>Retention deleted rows after the cursor: it resyncs; a cursor exactly at the floor lost nothing.</summary>
    [Fact]
    public async Task ACursorBelowTheRetentionFloor_Resyncs_OneAtTheFloorDoesNot()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var rows = await f.CommitAsync(false, f.Row("a"), f.Row("b"));
        var epoch = (await f.Feed.CurrentAsync()).Epoch; // creates the epoch row the floor lives on
        await using (var db = f.Harness.CreateContext())
        {
            await db.FeedEpochs.ExecuteUpdateAsync(u => u.SetProperty(e => e.FloorTxId, rows[0].TxId).SetProperty(e => e.FloorSeq, rows[0].Seq));
        }

        var below = new FeedCursor(epoch, new FeedPosition(rows[0].TxId, rows[0].Seq - 1)).ToString();
        var at = new FeedCursor(epoch, FeedPosition.Of(rows[0])).ToString();
        var viewer = FeedViewer.Setter(f.Harness.Owner.Id);

        Assert.True((await f.CatchUp.ReadWallAsync(f.WallId, viewer, below, null)).Resync);
        var fromFloor = await f.CatchUp.ReadWallAsync(f.WallId, viewer, at, null);
        Assert.False(fromFloor.Resync);
        Assert.Equal(["b"], fromFloor.Events.Select(e => e.Kind));
    }

    /// <summary>
    /// A short page moves the cursor to the newest stored row (of any wall), never past it, and a row written later is
    /// read from that cursor: the cap re-reads nothing and skips nothing.
    /// </summary>
    [Fact]
    public async Task AShortPagesNext_IsCappedAtTheNewestStoredRow_AndALaterRowIsStillRead()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var viewer = FeedViewer.Member(f.Member.Id);
        var newest = (await f.CommitAsync(false, f.Row("elsewhere", wallId: f.OtherWallId)))[0];

        var first = await f.CatchUp.ReadWallAsync(f.WallId, viewer, await f.BeginningAsync(), null);
        Assert.True(FeedCursor.TryParse(first.Next, out var next));
        Assert.Equal(FeedPosition.Of(newest), next.Position);
        Assert.Empty((await f.CatchUp.ReadWallAsync(f.WallId, viewer, first.Next, null)).Events);

        await f.CommitAsync(false, f.Row("later"));
        Assert.Equal(["later"], (await f.CatchUp.ReadWallAsync(f.WallId, viewer, first.Next, null)).Events.Select(e => e.Kind));
    }

    /// <summary>
    /// An outsider's next is the newest row of its own filter, not the global mark, so a share viewer cannot watch other
    /// walls' writes move it (the W4 side channel); a member's next is the global mark.
    /// </summary>
    [Fact]
    public async Task AnOutsidersNext_IgnoresOtherWallsWrites_AndStillReadsWhatFollows()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var start = await f.BeginningAsync();
        var before = await f.CatchUp.ReadWallAsync(f.WallId, FeedViewer.Anonymous(), start, null);
        await f.CommitAsync(false, f.Row("elsewhere", wallId: f.OtherWallId), f.Row("members-only", FeedAudience.Members));

        var after = await f.CatchUp.ReadWallAsync(f.WallId, FeedViewer.Anonymous(), before.Next, null);
        var member = await f.CatchUp.ReadWallAsync(f.WallId, FeedViewer.Member(f.Member.Id), start, null);

        Assert.Equal(before.Next, after.Next);
        Assert.Equal(before.Next, (await f.CatchUp.ReadWallAsync(f.WallId, FeedViewer.Anonymous(), "garbage", null)).Next);
        Assert.Equal((await f.Feed.CurrentAsync()).ToString(), member.Next);
        await f.CommitAsync(false, f.Row("shared"));
        Assert.Equal(["shared"], (await f.CatchUp.ReadWallAsync(f.WallId, FeedViewer.Anonymous(), after.Next, null)).Events.Select(e => e.Kind));
    }

    /// <summary>A cursor ahead of everything stored (forged, or of a lost dump) is brought back to the high-water mark.</summary>
    [Fact]
    public async Task AForgedCursorAheadOfTheMark_IsCappedAtIt()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var current = await f.Feed.CurrentAsync();
        var forged = new FeedCursor(current.Epoch, new FeedPosition(current.Position.TxId is null ? null : current.Position.TxId + 1_000_000, current.Position.Seq + 1_000_000));

        var page = await f.CatchUp.ReadWallAsync(f.WallId, FeedViewer.Setter(f.Harness.Owner.Id), forged.ToString(), null);

        Assert.False(page.Resync);
        Assert.Equal(current.ToString(), page.Next);
    }

    [Fact]
    public async Task FullPages_SayMore_AndThePagesTogetherAreTheFeedInOrder_Once()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        await f.CommitAsync(false, Enumerable.Range(0, 4).Select(i => f.Row($"p{i}")).ToArray());
        f.Caller = null;
        var after = await f.BeginningAsync();
        var seen = new List<string>();
        var pages = new List<bool>();
        FeedChanges page;
        do
        {
            page = FeedRouteFixture.Changes(await f.WallChangesAsync($"?after={after}&limit=2&token={FeedRouteFixture.ShareToken}"));
            seen.AddRange(page.Events.Select(e => e.Kind));
            pages.Add(page.More);
            after = page.Next;
        }
        while (page.More);

        Assert.Equal(["wall", "p0", "p1", "p2", "p3"], seen);
        Assert.Equal([true, true, false], pages);
        Assert.Empty(FeedRouteFixture.Changes(await f.WallChangesAsync($"?after={after}&limit=2&token={FeedRouteFixture.ShareToken}")).Events);
    }

    [Fact]
    public async Task TheLimit_IsClamped()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var viewer = FeedViewer.Setter(f.Harness.Owner.Id);
        var start = await f.BeginningAsync();

        var one = await f.CatchUp.ReadWallAsync(f.WallId, viewer, start, 0);
        Assert.Single(one.Events);
        Assert.True(one.More);
        Assert.False((await f.CatchUp.ReadWallAsync(f.WallId, viewer, start, 50_000)).More);
    }

    [Fact]
    public async Task Comments_AreHydrated_AGoneOneIsATombstone_AndActorsFollowAuthorVisibility()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var (kept, gone, erased) = await CommentsAsync(f);
        await f.CommitAsync(
            false,
            f.Row(FeedKinds.CommentAdded, actor: f.Member.Id, entityId: kept),
            f.Row(FeedKinds.CommentAdded, actor: f.Member.Id, entityId: gone),
            f.Row(FeedKinds.CommentAdded, actor: erased, entityId: Guid.NewGuid()));
        var start = await f.BeginningAsync();

        var member = (await f.CatchUp.ReadWallAsync(f.WallId, FeedViewer.Member(f.Member.Id), start, null)).Events
            .Where(e => e.Kind == FeedKinds.CommentAdded).ToList();
        var outsider = (await f.CatchUp.ReadWallAsync(f.WallId, FeedViewer.Anonymous(), start, null)).Events
            .Where(e => e.Kind == FeedKinds.CommentAdded).ToList();

        Assert.Equal("kept text", member[0].Hydrated!.Text);
        Assert.False(member[0].Tombstone);
        Assert.True(member[1].Tombstone);
        Assert.Equal((f.Member.Id, "member@test"), (member[0].Actor!.Id, member[0].Actor!.Name));
        Assert.Equal((null, "member@test"), (outsider[0].Actor!.Id, outsider[0].Actor!.Name));
        Assert.Equal((GhostUser.Id, PlaceholderIdentity.DisplayName), (member[2].Actor!.Id, member[2].Actor!.Name));
        Assert.Null(outsider[2].Actor!.Id);
    }

    private static async Task<(Guid Kept, Guid Gone, Guid Erased)> CommentsAsync(FeedRouteFixture f)
    {
        await using var db = f.Harness.CreateContext();
        var boulder = new Boulder { WallId = f.WallId, Name = "B", CreatedByUserId = f.Harness.Owner.Id };
        var erased = new User { Identifier = "erased@test", DisplayName = "Erased", DeletedAt = DateTimeOffset.UtcNow };
        var comment = new BoulderComment { BoulderId = boulder.Id, UserId = f.Member.Id, Text = "kept text" };
        db.Boulders.Add(boulder);
        db.Users.Add(erased);
        db.BoulderComments.Add(comment);
        await db.SaveChangesAsync();
        return (comment.Id, Guid.NewGuid(), erased.Id);
    }
}
