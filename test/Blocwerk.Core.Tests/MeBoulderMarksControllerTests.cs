// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.Projects;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>/api/v1/me/{ratings,favorites,projects,comments}</c> for the companion app: the offline client's writes, answered
/// with its envelope. A member's key writes its own marks, a replay is a no-op, someone else's wall or comment is refused,
/// and a bad body is a permanent 400. Who may call at all is <see cref="DeviceApiRoutePostureTests"/>.
/// </summary>
public sealed class MeBoulderMarksControllerTests
{
    [Fact]
    public async Task Rating_IsSet_Replayed_AndRemoved()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedAsync(h);
        var controller = Controller(h);

        AssertApplied(await controller.SetRating(new SetRatingRequest { BoulderId = boulder, Stars = 4 }));
        AssertApplied(await controller.SetRating(new SetRatingRequest { BoulderId = boulder, Stars = 4 }));
        Assert.Equal(4, (await h.FeedbackService.GetRatingAsync(boulder)).MyRating);
        Assert.Equal(1, (await h.FeedbackService.GetRatingAsync(boulder)).Count);

        AssertApplied(await controller.RemoveRating(boulder));
        AssertApplied(await controller.RemoveRating(boulder));
        Assert.Null((await h.FeedbackService.GetRatingAsync(boulder)).MyRating);
    }

    [Fact]
    public async Task FavoriteAndProject_AreSetAndRemoved_AsAbsoluteValues()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedAsync(h);
        var controller = Controller(h);

        AssertApplied(await controller.SetFavorite(new SetFavoriteRequest { BoulderId = boulder, Favorite = true }));
        AssertApplied(await controller.SetFavorite(new SetFavoriteRequest { BoulderId = boulder, Favorite = true }));
        AssertApplied(await controller.SetProject(new SetProjectRequest { BoulderId = boulder, Project = true }));
        Assert.True(await h.FeedbackService.IsFavoritedAsync(boulder));
        Assert.True(await Projects(h).IsProjectAsync(boulder));

        AssertApplied(await controller.RemoveFavorite(boulder));
        AssertApplied(await controller.RemoveProject(boulder));
        Assert.False(await h.FeedbackService.IsFavoritedAsync(boulder));
        Assert.False(await Projects(h).IsProjectAsync(boulder));
    }

    [Fact]
    public async Task Comment_IsPostedOncePerClientRequestId_AndDeleted()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedAsync(h);
        var controller = Controller(h);
        var requestId = Guid.NewGuid();
        var body = new AddCommentRequest { BoulderId = boulder, Text = "Nice heel", ClientRequestId = requestId };

        var first = AssertApplied(await controller.AddComment(body));
        AssertApplied(await controller.AddComment(body));
        Assert.Equal(requestId, first.ClientRequestId);
        await using (var db = h.CreateContext())
        {
            var stored = Assert.Single(await db.BoulderComments.Where(c => c.BoulderId == boulder).ToListAsync());
            Assert.Equal(h.Owner.Id, stored.UserId);
            AssertApplied(await controller.DeleteComment(stored.Id));
        }

        await using var check = h.CreateContext();
        Assert.Empty(await check.BoulderComments.Where(c => c.BoulderId == boulder).ToListAsync());
    }

    [Fact]
    public async Task AnOutsidersKey_IsRefusedOnEveryWrite_AndCannotDeleteSomeoneElsesComment()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedAsync(h);
        var comment = await new CommentService(h.DbContextFactory, h.CurrentUser, h.ActivityLog, NullLogger<CommentService>.Instance)
            .AddCommentAsync(boulder, "mine");
        h.ActingUser = await AddUserAsync(h, "outsider@test");
        var controller = Controller(h);

        AssertRefused(await controller.SetRating(new SetRatingRequest { BoulderId = boulder, Stars = 3 }), 403);
        AssertRefused(await controller.SetFavorite(new SetFavoriteRequest { BoulderId = boulder, Favorite = true }), 403);
        AssertRefused(await controller.SetProject(new SetProjectRequest { BoulderId = boulder, Project = true }), 403);
        AssertRefused(await controller.AddComment(new AddCommentRequest { BoulderId = boulder, Text = "hi" }), 404);
        AssertRefused(await controller.DeleteComment(comment.Id), 404);

        await using var db = h.CreateContext();
        Assert.Single(await db.BoulderComments.ToListAsync());
        Assert.Empty(await db.BoulderRatings.ToListAsync());
        Assert.Empty(await db.BoulderFavorites.ToListAsync());
    }

    [Fact]
    public async Task AMemberCannotDeleteAnotherMembersComment()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedAsync(h);
        var comment = await new CommentService(h.DbContextFactory, h.CurrentUser, h.ActivityLog, NullLogger<CommentService>.Instance)
            .AddCommentAsync(boulder, "the owner's");
        h.ActingUser = await h.AddMemberAsync("member@test", Enums.WallRole.Member);

        AssertRefused(await Controller(h).DeleteComment(comment.Id), 404);
    }

    [Fact]
    public async Task ABadBody_IsAPermanent400()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedAsync(h);
        var controller = Controller(h);

        AssertRefused(await controller.SetRating(new SetRatingRequest { BoulderId = boulder, Stars = 6 }), 400);
        AssertRefused(await controller.SetRating(new SetRatingRequest { BoulderId = Guid.Empty, Stars = 3 }), 400);
        AssertRefused(await controller.SetFavorite(new SetFavoriteRequest { BoulderId = Guid.Empty, Favorite = true }), 400);
        AssertRefused(await controller.AddComment(new AddCommentRequest { BoulderId = boulder, Text = "  " }), 400);
        AssertRefused(await controller.AddComment(new AddCommentRequest { BoulderId = boulder, Text = new string('x', 1025) }), 400);
        AssertRefused(await controller.SetRating(new SetRatingRequest { BoulderId = Guid.NewGuid(), Stars = 3 }), 404);
    }

    private static OfflineActionResponse AssertApplied(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<OfflineActionResponse>(ok.Value);
        Assert.True(body.Applied);
        return body;
    }

    private static void AssertRefused(IActionResult result, int status)
    {
        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, refused.StatusCode);
        Assert.Equal(status != 500, Assert.IsType<OfflineActionError>(refused.Value).Permanent);
    }

    private static MeBoulderMarksController Controller(WallTestHarness h) =>
        new(h.FeedbackService, Projects(h), new CommentService(h.DbContextFactory, h.CurrentUser, h.ActivityLog, NullLogger<CommentService>.Instance))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() },
            },
        };

    private static BoulderProjectService Projects(WallTestHarness h) =>
        new(h.DbContextFactory, h.CurrentUser, Substitute.For<IKioskContext>(), h.FeedbackService, NullLogger<BoulderProjectService>.Instance);

    private static async Task<Guid> SeedAsync(WallTestHarness h)
    {
        await h.SeedWallAsync(holdCount: 0);
        return await VisitFixture.SeedBoulderAsync(h, h.WallId);
    }

    private static async Task<User> AddUserAsync(WallTestHarness h, string identifier)
    {
        await using var db = h.CreateContext();
        var user = new User { Identifier = identifier, DisplayName = identifier };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
