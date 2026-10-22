// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Comment rows a share link or a kiosk tablet replays after its comments document: a comment on an archived boulder is
/// written for members only, and a comment whose boulder went back to draft (or was archived) after it was written is not
/// handed out at delivery, neither in the catch-up nor live. Members still get both.
/// </summary>
public sealed class OutsideCommentRowsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACommentOnABoulderTakenBackToDraft_IsNotReplayedOutside_ButIsToMembers(bool kiosk)
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var boulderId = await AddBoulderAsync(f, archived: false);
        var commentId = await CommentAsync(f, boulderId, "said while it was published");
        Assert.Equal(FeedAudience.Wall, (await StoredAsync(f, commentId)).Audience);
        await using (var db = StructureFeedKit.Context(f.Harness))
        {
            (await db.Boulders.SingleAsync(b => b.Id == boulderId)).IsDraft = true;
            await db.SaveChangesAsync();
        }

        Assert.Contains(await MemberReplayAsync(f), e => e.EntityId == commentId && e.Hydrated?.Text == "said while it was published");
        var outside = await OutsideReplayAsync(f, kiosk);
        Assert.DoesNotContain(outside, e => e.EntityId == commentId || e.Kind == FeedKinds.CommentAdded);
        Assert.DoesNotContain("said while it was published", string.Join("|", outside.Select(e => e.Hydrated?.Text)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACommentOnAnArchivedBoulder_IsWrittenForMembers_AndNotReplayedOutside(bool kiosk)
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var boulderId = await AddBoulderAsync(f, archived: true);
        var commentId = await CommentAsync(f, boulderId, "archived chat");

        Assert.Equal(FeedAudience.Members, (await StoredAsync(f, commentId)).Audience);
        Assert.Contains(await MemberReplayAsync(f), e => e.EntityId == commentId && e.Hydrated?.Text == "archived chat");
        Assert.DoesNotContain(await OutsideReplayAsync(f, kiosk), e => e.EntityId == commentId);
    }

    /// <summary>The live path: a Wall row of a boulder that is a draft by the time the hub delivers it reaches members only.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALiveCommentRow_OfABoulderNowDraftOrArchived_ReachesMembersButNoOutsideStream(bool kiosk)
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var draft = await AddBoulderAsync(f, archived: false, draft: true);
        var archived = await AddBoulderAsync(f, archived: true);
        var after = await f.BeginningAsync();
        var outsideQuery = Outside(f, kiosk);
        await using var outside = FeedStreamRun.Open(f, $"?after={after}{outsideQuery}");
        await outside.Body.WaitUntilAsync(b => b.Events.Any(e => e.Kind == "wall"));

        await f.CommitAsync(true, CommentRow(f, draft), CommentRow(f, archived), f.Row("live"));
        await outside.Body.WaitUntilAsync(b => b.Events.Any(e => e.Kind == "live"));
        Assert.DoesNotContain(outside.Body.Events, e => e.Kind == FeedKinds.CommentAdded);
        await MemberSeesEveryLiveCommentAsync(f, draft);
    }

    private static async Task MemberSeesEveryLiveCommentAsync(FeedRouteFixture f, Guid draft)
    {
        f.Kiosk.IsKiosk.Returns(false);
        f.Caller = f.Member;
        await using var member = FeedStreamRun.Open(f, "?after=" + await f.BeginningAsync());
        await member.Body.WaitUntilAsync(b => b.Events.Any(e => e.Kind == "live"));
        await f.CommitAsync(true, CommentRow(f, draft), f.Row("live2"));
        await member.Body.WaitUntilAsync(b => b.Events.Any(e => e.Kind == "live2"));
        Assert.Equal(3, member.Body.Events.Count(e => e.Kind == FeedKinds.CommentAdded));
    }

    private static string Outside(FeedRouteFixture f, bool kiosk)
    {
        if (kiosk)
        {
            f.BindKiosk(f.WallId);
            f.Caller = f.Member;
            return string.Empty;
        }

        f.Caller = null;
        return $"&token={FeedRouteFixture.ShareToken}";
    }

    private static async Task<IReadOnlyList<FeedEnvelope>> OutsideReplayAsync(FeedRouteFixture f, bool kiosk)
    {
        var query = Outside(f, kiosk);
        var after = await f.BeginningAsync();
        var replay = FeedRouteFixture.Changes(await f.WallChangesAsync($"?after={after}{query}")).Events;
        await using var run = FeedStreamRun.Open(f, $"?after={after}{query}");
        await run.Body.WaitUntilAsync(b => b.Events.Any(e => e.Kind == "wall"));
        return [.. replay, .. run.Body.Events];
    }

    private static async Task<IReadOnlyList<FeedEnvelope>> MemberReplayAsync(FeedRouteFixture f)
    {
        f.Caller = f.Member;
        return FeedRouteFixture.Changes(await f.WallChangesAsync("?after=" + await f.BeginningAsync())).Events;
    }

    private static async Task<Guid> AddBoulderAsync(FeedRouteFixture f, bool archived, bool draft = false)
    {
        await using var db = f.Harness.CreateContext();
        var boulder = new Boulder { WallId = f.WallId, Name = "B", CreatedByUserId = f.Harness.Owner.Id, IsArchived = archived, IsDraft = draft };
        db.Boulders.Add(boulder);
        await db.SaveChangesAsync();
        return boulder.Id;
    }

    // Through the real interceptor, so the row is placed (and narrowed) as the app writes it.
    private static async Task<Guid> CommentAsync(FeedRouteFixture f, Guid boulderId, string text)
    {
        await using var db = StructureFeedKit.Context(f.Harness);
        var comment = new BoulderComment { BoulderId = boulderId, UserId = f.Member.Id, Text = text };
        db.BoulderComments.Add(comment);
        await db.SaveChangesAsync();
        return comment.Id;
    }

    private static async Task<WallChange> StoredAsync(FeedRouteFixture f, Guid commentId)
    {
        await using var db = f.Harness.CreateContext();
        return await db.WallChanges.AsNoTracking().SingleAsync(c => c.Kind == FeedKinds.CommentAdded && c.EntityId == commentId);
    }

    // A Wall row as one written before its boulder became a draft or archived.
    private static WallChange CommentRow(FeedRouteFixture f, Guid boulderId)
    {
        var row = f.Row(FeedKinds.CommentAdded, actor: f.Member.Id);
        row.BoulderId = boulderId;
        return row;
    }
}
