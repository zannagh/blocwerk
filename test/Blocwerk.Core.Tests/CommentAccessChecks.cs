// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The comment reads and writes, per viewer, shared by the SQLite and the PostgreSQL runs. Comments follow the
/// boulder: listed for whoever may see it (drafts for members only), written and deleted by members only, and a kiosk
/// acting as a member stays on the tablet's wall.
/// </summary>
internal static class CommentAccessChecks
{
    public static async Task ListAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        await SeedCommentAsync(h, s.LiveId, s.Member.Id, "on the live one");
        await SeedCommentAsync(h, s.DraftId, s.Member.Id, "on the draft");
        var comments = s.Comments(viewer);

        var live = await comments.GetCommentsAsync(s.LiveId, shareToken: s.TokenFor(viewer));
        var draft = await comments.GetCommentsAsync(s.DraftId, shareToken: s.TokenFor(viewer));

        Assert.Equal(("on the live one", 1), (Assert.Single(live.Items).Text, live.TotalCount));
        Assert.Equal(DraftExposureScenario.SeesDrafts(viewer) ? 1 : 0, draft.TotalCount);
        Assert.Equal(draft.TotalCount, draft.Items.Count);
    }

    /// <summary>Signed in but not a member, without a share link, and anonymous with no kiosk: nothing at all.</summary>
    public static async Task ListForStrangersAsync(WallTestHarness h)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        await SeedCommentAsync(h, s.LiveId, s.Member.Id, "members only");

        var stranger = await Plain(h, signedIn: new User { Identifier = "stranger@test", DisplayName = "Stranger" })
            .GetCommentsAsync(s.LiveId);
        var anonymous = await Plain(h, signedIn: null).GetCommentsAsync(s.LiveId);
        var wrongToken = await s.Comments(DraftViewer.ShareAnonymous).GetCommentsAsync(s.LiveId, shareToken: "not-the-token");

        Assert.All(new[] { stranger, anonymous, wrongToken }, page => Assert.Equal((0, 0), (page.Items.Count, page.TotalCount)));
    }

    public static async Task AddAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var comments = s.Comments(viewer);

        if (DraftExposureScenario.SeesDrafts(viewer))
        {
            Assert.Equal(s.Member.Id, (await comments.AddCommentAsync(s.LiveId, "nice")).UserId);
            Assert.Equal(s.Member.Id, (await comments.AddCommentAsync(s.DraftId, "draft note")).UserId);
            Assert.Equal(2, await CountAsync(h));
            return;
        }

        // Nobody signed in: the session check refuses before any boulder is read. Signed in but not a member: the
        // same not-found as a missing boulder, for the live boulder as much as for the draft.
        if (viewer == DraftViewer.ShareNonMember)
        {
            await AssertNotFoundAsync(() => comments.AddCommentAsync(s.LiveId, "drive-by"));
            await AssertNotFoundAsync(() => comments.AddCommentAsync(s.DraftId, "drive-by"));
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => comments.AddCommentAsync(s.LiveId, "anon"));
        }

        Assert.Equal(0, await CountAsync(h));
    }

    public static async Task DeleteAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var memberComment = await SeedCommentAsync(h, s.LiveId, s.Member.Id, "mine");
        var ownerComment = await SeedCommentAsync(h, s.LiveId, h.Owner.Id, "the owner's");
        var comments = s.Comments(viewer);

        // Nobody may delete somebody else's comment.
        await Assert.ThrowsAnyAsync<Exception>(() => comments.DeleteCommentAsync(ownerComment));

        if (DraftExposureScenario.SeesDrafts(viewer))
        {
            await comments.DeleteCommentAsync(memberComment);
            Assert.Equal(1, await CountAsync(h));
            return;
        }

        await Assert.ThrowsAnyAsync<Exception>(() => comments.DeleteCommentAsync(memberComment));
        Assert.Equal(2, await CountAsync(h));
    }

    /// <summary>
    /// The member is on a second wall too. From their own session they may comment and delete there; from the
    /// tablet acting as them, the kiosk wall gate keeps both writes on the tablet's wall.
    /// </summary>
    public static async Task KioskActAsStaysOnItsWallAsync(WallTestHarness h)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var otherBoulder = await SeedOtherWallAsync(h, s.Member.Id);
        var ownComment = await SeedCommentAsync(h, otherBoulder, s.Member.Id, "elsewhere");

        var kiosk = s.Comments(DraftViewer.KioskActAsMember);
        await AssertNotFoundAsync(() => kiosk.AddCommentAsync(otherBoulder, "from the tablet"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kiosk.DeleteCommentAsync(ownComment));
        Assert.Equal(0, (await kiosk.GetCommentsAsync(otherBoulder)).TotalCount);

        var own = s.Comments(DraftViewer.Member);
        await own.AddCommentAsync(otherBoulder, "from my phone");
        await own.DeleteCommentAsync(ownComment);
        Assert.Equal("from my phone", Assert.Single((await own.GetCommentsAsync(otherBoulder)).Items).Text);
    }

    private static CommentService Plain(WallTestHarness h, User? signedIn)
    {
        var users = Substitute.For<ICurrentUserService>();
        users.GetCurrentUserAsync().Returns(_ => signedIn is null
            ? Task.FromException<User>(new UnauthorizedAccessException())
            : Task.FromResult(signedIn));
        return new CommentService(h.DbContextFactory, users, h.ActivityLog, NullLogger<CommentService>.Instance);
    }

    private static async Task AssertNotFoundAsync(Func<Task> add)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(add);
        Assert.Equal("Boulder not found", ex.Message);
    }

    private static async Task<Guid> SeedCommentAsync(WallTestHarness h, Guid boulderId, Guid userId, string text)
    {
        await using var db = h.CreateContext();
        var comment = new BoulderComment { BoulderId = boulderId, UserId = userId, Text = text };
        db.BoulderComments.Add(comment);
        await db.SaveChangesAsync();
        return comment.Id;
    }

    private static async Task<Guid> SeedOtherWallAsync(WallTestHarness h, Guid memberId)
    {
        await using var db = h.CreateContext();
        var wall = new Wall { Name = "Other Wall", OwnerId = h.Owner.Id, Photo = [1], PhotoContentType = "image/jpeg" };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = memberId, Role = WallRole.Member });
        var boulder = new Boulder { WallId = wall.Id, Name = "Elsewhere", CreatedByUserId = h.Owner.Id };
        db.Boulders.Add(boulder);
        await db.SaveChangesAsync();
        return boulder.Id;
    }

    private static async Task<int> CountAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.BoulderComments.CountAsync();
    }
}
