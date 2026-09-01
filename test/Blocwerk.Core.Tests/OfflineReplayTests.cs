using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Ascents, comments, ratings and favorites are queued client-side while offline and
/// replayed after a reconnect, so every one of those writes has to survive being applied
/// twice without duplicating anything.
/// </summary>
public class OfflineReplayTests
{
    [Fact]
    public async Task LogAttempt_WithTheSameClientRequestId_CreatesOneRow()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        var requestId = Guid.NewGuid();
        var first = await h.AttemptService.LogAttemptAsync(boulder.Id, AttemptType.Send, clientRequestId: requestId);
        var replay = await h.AttemptService.LogAttemptAsync(boulder.Id, AttemptType.Send, clientRequestId: requestId);

        Assert.Equal(first.Id, replay.Id);

        await using var check = h.CreateContext();
        var stored = await check.Attempts.Where(a => a.BoulderId == boulder.Id).ToListAsync();
        Assert.Single(stored);
        Assert.Equal(requestId, stored[0].ClientRequestId);
    }

    [Fact]
    public async Task LogAttempt_SameActionTwiceWithinAMinute_IsDebouncedToOneRow()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        // Nobody logs the same action on the same boulder twice within a minute, so the second
        // call is treated as an accidental duplicate and silently returns the first row.
        var first = await h.AttemptService.LogAttemptAsync(boulder.Id, AttemptType.Attempt);
        var second = await h.AttemptService.LogAttemptAsync(boulder.Id, AttemptType.Attempt);

        Assert.Equal(first.Id, second.Id);

        await using var check = h.CreateContext();
        Assert.Equal(1, await check.Attempts.CountAsync(a => a.BoulderId == boulder.Id));
    }

    [Fact]
    public async Task LogAttempt_SameActionMoreThanAMinuteApart_LogsBoth()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        var t0 = DateTimeOffset.UtcNow;
        await h.AttemptService.LogAttemptAsync(boulder.Id, AttemptType.Attempt, timestamp: t0);
        await h.AttemptService.LogAttemptAsync(boulder.Id, AttemptType.Attempt, timestamp: t0.AddMinutes(2));

        await using var check = h.CreateContext();
        Assert.Equal(2, await check.Attempts.CountAsync(a => a.BoulderId == boulder.Id));
    }

    [Fact]
    public async Task OfflineAttemptEndpoint_ForwardsTheClientCapturedTimestamp()
    {
        // The whole point of FIX: an offline batch replays at reconnect time, but each queued
        // attempt carries the real moment of the tap. If the controller dropped that timestamp,
        // the service would anchor its 60s debounce on "now" and a spaced-apart batch would
        // collapse. This proves the offline endpoint threads the timestamp into the service.
        var attemptService = Substitute.For<IAttemptService>();
        var boulderId = Guid.NewGuid();
        var tapTime = new DateTimeOffset(2026, 8, 27, 9, 15, 0, TimeSpan.Zero);
        attemptService
            .LogAttemptAsync(Arg.Any<Guid>(), Arg.Any<AttemptType>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<DateTimeOffset?>())
            .Returns(new Attempt { Id = Guid.NewGuid(), BoulderId = boulderId, Type = AttemptType.Attempt });

        var controller = new OfflineActionsController(
            attemptService,
            Substitute.For<IBoulderFeedbackService>(),
            Substitute.For<ICommentService>(),
            Substitute.For<ICurrentUserService>());

        var request = new LogAttemptRequest
        {
            BoulderId = boulderId,
            Type = AttemptType.Attempt,
            ClientRequestId = Guid.NewGuid(),
            Timestamp = tapTime,
        };

        var result = await controller.LogAttempt(request);

        Assert.IsType<OkObjectResult>(result);
        await attemptService.Received(1).LogAttemptAsync(
            boulderId,
            AttemptType.Attempt,
            Arg.Any<string?>(),
            request.ClientRequestId,
            tapTime);
    }

    [Fact]
    public async Task LogAttempt_DifferentActionsWithinAMinute_BothLog()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        // The debounce is keyed on the action type, so an attempt and a send do not suppress each other.
        await h.AttemptService.LogAttemptAsync(boulder.Id, AttemptType.Attempt);
        await h.AttemptService.LogAttemptAsync(boulder.Id, AttemptType.Send);

        await using var check = h.CreateContext();
        Assert.Equal(2, await check.Attempts.CountAsync(a => a.BoulderId == boulder.Id));
    }

    [Fact]
    public async Task AddComment_WithTheSameClientRequestId_CreatesOneRow()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        var commentService = new CommentService(h.DbContextFactory, h.CurrentUser, h.ActivityLog, NullLogger<CommentService>.Instance);
        var requestId = Guid.NewGuid();

        var first = await commentService.AddCommentAsync(boulder.Id, "Nice", requestId);
        var replay = await commentService.AddCommentAsync(boulder.Id, "Nice", requestId);

        Assert.Equal(first.Id, replay.Id);

        await using var check = h.CreateContext();
        Assert.Single(await check.BoulderComments.Where(c => c.BoulderId == boulder.Id).ToListAsync());
    }

    [Fact]
    public async Task SetRating_IsAnUpsert_SoAReplayIsANoOp()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        await h.FeedbackService.SetRatingAsync(boulder.Id, 4);
        await h.FeedbackService.SetRatingAsync(boulder.Id, 4);

        var info = await h.FeedbackService.GetRatingAsync(boulder.Id);
        Assert.Equal(1, info.Count);
        Assert.Equal(4, info.MyRating);
    }

    [Fact]
    public async Task CreateBoulder_WithTheSameClientId_CreatesOneBoulder()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);

        var clientId = Guid.NewGuid();
        var first = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", "6A", [new BoulderHoldInput(holds[0].Id)], id: clientId);
        var replay = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", "6A", [new BoulderHoldInput(holds[0].Id)], id: clientId);

        Assert.Equal(clientId, first.Id);
        Assert.Equal(first.Id, replay.Id);

        await using var check = h.CreateContext();
        Assert.Single(await check.Boulders.Where(b => b.WallId == h.WallId).ToListAsync());
    }

    [Fact]
    public async Task ReviseBoulder_ReplayedTwice_LeavesTheSameFinalState()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 3);

        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Old", "6A", [new BoulderHoldInput(holds[0].Id)]);

        await using (var db = h.CreateContext())
        {
            var b = await db.Boulders.FirstAsync(x => x.Id == boulder.Id);
            b.IsHistoric = true;
            b.NeedsReview = true;
            await db.SaveChangesAsync();
        }

        var newHolds = new List<BoulderHoldInput> { new(holds[2].Id, HoldType.Start) };

        // First apply flips the boulder historic -> live; the replay must be a no-op, not throw.
        var first = await h.BoulderService.ReviseBoulderAsync(
            boulder.Id, newHolds, name: "New", grade: "6B");
        var replay = await h.BoulderService.ReviseBoulderAsync(
            boulder.Id, newHolds, name: "New", grade: "6B");

        Assert.Equal(first.Id, replay.Id);

        await using var check = h.CreateContext();
        var revised = await check.Boulders
            .Include(b => b.BoulderHolds)
            .FirstAsync(b => b.Id == boulder.Id);

        Assert.False(revised.IsHistoric);
        Assert.Equal("New", revised.Name);
        Assert.Equal("6B", revised.Grade);
        Assert.Equal(holds[2].Id, Assert.Single(revised.BoulderHolds).HoldId);
    }

    [Fact]
    public async Task SetFavorite_IsIdempotent_UnlikeToggle()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        Assert.True(await h.FeedbackService.SetFavoriteAsync(boulder.Id, true));
        Assert.True(await h.FeedbackService.SetFavoriteAsync(boulder.Id, true));
        Assert.True(await h.FeedbackService.IsFavoritedAsync(boulder.Id));

        await using var check = h.CreateContext();
        Assert.Single(await check.BoulderFavorites.Where(f => f.BoulderId == boulder.Id).ToListAsync());

        // The toggle is the interactive path and deliberately flips on every call.
        Assert.False(await h.FeedbackService.ToggleFavoriteAsync(boulder.Id));
    }
}
