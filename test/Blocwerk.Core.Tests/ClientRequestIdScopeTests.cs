// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A <c>clientRequestId</c> is a replay key for its own sender only. The same id sent by someone else is their own new
/// write, never an answer with the first sender's row, and the empty id is refused.
/// </summary>
public sealed class ClientRequestIdScopeTests
{
    [Fact]
    public async Task AnAttemptWithAnotherUsersId_IsANewAttempt_NotTheirReplay()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var boulder = await VisitFixture.SeedBoulderAsync(h, h.WallId);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        var fixedId = Guid.NewGuid();

        var owners = await h.AttemptService.LogAttemptAsync(boulder, AttemptType.Send, clientRequestId: fixedId);
        h.ActingUser = member;
        var members = await h.AttemptService.LogAttemptAsync(boulder, AttemptType.Send, clientRequestId: fixedId);

        Assert.NotEqual(owners.Id, members.Id);
        Assert.Equal(member.Id, members.UserId);
        await using var db = h.CreateContext();
        Assert.Equal(2, await db.Attempts.CountAsync(a => a.ClientRequestId == fixedId));
    }

    [Fact]
    public async Task ACommentWithAnotherUsersId_IsANewComment_NotTheirReplay()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var boulder = await VisitFixture.SeedBoulderAsync(h, h.WallId);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        var comments = new CommentService(h.DbContextFactory, h.CurrentUser, h.ActivityLog, NullLogger<CommentService>.Instance);
        var fixedId = Guid.NewGuid();

        var owners = await comments.AddCommentAsync(boulder, "the owner's", fixedId);
        h.ActingUser = member;
        var members = await comments.AddCommentAsync(boulder, "the member's", fixedId);

        Assert.NotEqual(owners.Id, members.Id);
        Assert.Equal("the member's", members.Text);
        Assert.Equal(members.Id, (await comments.AddCommentAsync(boulder, "the member's", fixedId)).Id);
    }

    [Fact]
    public async Task TheEmptyId_IsRefused()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var boulder = await VisitFixture.SeedBoulderAsync(h, h.WallId);
        var comments = new CommentService(h.DbContextFactory, h.CurrentUser, h.ActivityLog, NullLogger<CommentService>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => h.AttemptService.LogAttemptAsync(boulder, AttemptType.Send, clientRequestId: Guid.Empty));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => comments.AddCommentAsync(boulder, "x", Guid.Empty));
    }
}
