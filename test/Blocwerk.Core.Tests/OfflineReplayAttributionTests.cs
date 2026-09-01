using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The offline queue lives in IndexedDB, which is per browser profile — not per user. A queued
/// action used to carry no identity at all, so it was attributed to whoever happened to be signed
/// in when the queue drained. On a kiosk tablet (or any shared browser) that is a different person:
/// A logs a send while the link is flaky, releases the tablet, B picks themselves, the queue
/// flushes, and A's climb lands in B's logbook.
/// <para>
/// The client now stamps <c>queuedForUserId</c> at enqueue time and the server refuses a mismatch
/// with 409, which keeps the entry queued for its real owner instead of writing it as the wrong
/// person. These tests pin all three cases: mismatch refuses and writes nothing, a match behaves
/// exactly as before, and an unstamped legacy entry keeps the old behaviour.
/// </para>
/// </summary>
public class OfflineReplayAttributionTests
{
    #region attempts

    [Fact]
    public async Task Attempt_StampedForAnotherUser_IsRefusedAndNothingIsWritten()
    {
        var attemptService = Substitute.For<IAttemptService>();
        var controller = ControllerFor(attemptService, out var signedInUserId);

        var result = await controller.LogAttempt(new LogAttemptRequest
        {
            BoulderId = Guid.NewGuid(),
            Type = AttemptType.Send,
            ClientRequestId = Guid.NewGuid(),
            QueuedForUserId = Guid.NewGuid(), // somebody else
        });

        Assert.NotEqual(Guid.Empty, signedInUserId);
        AssertHeld(result);
        await attemptService.ReceivedWithAnyArgs(0).LogAttemptAsync(default, default);
    }

    [Fact]
    public async Task Attempt_StampedForTheSignedInUser_IsAppliedNormally()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        // The single-user case, which is the overwhelming majority of devices: the stamp is always
        // the person signed in, so replay is byte-for-byte the old behaviour.
        var controller = HarnessController(h);
        var result = await controller.LogAttempt(new LogAttemptRequest
        {
            BoulderId = boulder.Id,
            Type = AttemptType.Send,
            ClientRequestId = Guid.NewGuid(),
            QueuedForUserId = h.Owner.Id,
        });

        Assert.IsType<OkObjectResult>(result);

        await using var check = h.CreateContext();
        var stored = await check.Attempts.SingleAsync(a => a.BoulderId == boulder.Id);
        Assert.Equal(h.Owner.Id, stored.UserId);
    }

    [Fact]
    public async Task Attempt_QueuedByOneUserAndReplayedByAnother_NeverLandsOnTheSecondUser()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);
        var second = await h.AddMemberAsync("second@test", WallRole.Member);

        // The exact kiosk sequence: Owner queues a send, then releases, then `second` picks
        // themselves, and only then does the queue flush.
        h.ActingUser = second;

        var controller = HarnessController(h);
        var result = await controller.LogAttempt(new LogAttemptRequest
        {
            BoulderId = boulder.Id,
            Type = AttemptType.Send,
            ClientRequestId = Guid.NewGuid(),
            QueuedForUserId = h.Owner.Id,
        });

        AssertHeld(result);

        await using var check = h.CreateContext();
        Assert.Empty(await check.Attempts.Where(a => a.BoulderId == boulder.Id).ToListAsync());
    }

    /// <summary>
    /// Migration: entries already sitting in a browser's IndexedDB when stamping shipped have no
    /// stamp and no recoverable owner. They keep the pre-existing behaviour rather than being
    /// destroyed or stranded forever — a one-time drain that cannot make any device worse than it
    /// already was.
    /// </summary>
    [Fact]
    public async Task Attempt_WithNoStamp_StillRepliesForWhoeverIsSignedIn()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);
        var second = await h.AddMemberAsync("legacy@test", WallRole.Member);
        h.ActingUser = second;

        var controller = HarnessController(h);
        var result = await controller.LogAttempt(new LogAttemptRequest
        {
            BoulderId = boulder.Id,
            Type = AttemptType.Attempt,
            ClientRequestId = Guid.NewGuid(),
            QueuedForUserId = null,
        });

        Assert.IsType<OkObjectResult>(result);

        await using var check = h.CreateContext();
        var stored = await check.Attempts.SingleAsync(a => a.BoulderId == boulder.Id);
        Assert.Equal(second.Id, stored.UserId);
    }

    #endregion

    #region the other action types

    [Fact]
    public async Task Comment_StampedForAnotherUser_IsRefusedAndNothingIsWritten()
    {
        var commentService = Substitute.For<ICommentService>();
        var controller = new OfflineActionsController(
            Substitute.For<IAttemptService>(),
            Substitute.For<IBoulderFeedbackService>(),
            commentService,
            SignedInAs(Guid.NewGuid()));

        var result = await controller.AddComment(new AddCommentRequest
        {
            BoulderId = Guid.NewGuid(),
            Text = "nice one",
            ClientRequestId = Guid.NewGuid(),
            QueuedForUserId = Guid.NewGuid(),
        });

        AssertHeld(result);
        await commentService.ReceivedWithAnyArgs(0).AddCommentAsync(default, default!);
    }

    [Fact]
    public async Task Rating_StampedForAnotherUser_IsRefusedAndNothingIsWritten()
    {
        var feedbackService = Substitute.For<IBoulderFeedbackService>();
        var controller = new OfflineActionsController(
            Substitute.For<IAttemptService>(),
            feedbackService,
            Substitute.For<ICommentService>(),
            SignedInAs(Guid.NewGuid()));

        var result = await controller.SetRating(new SetRatingRequest
        {
            BoulderId = Guid.NewGuid(),
            Stars = 3,
            QueuedForUserId = Guid.NewGuid(),
        });

        AssertHeld(result);
        await feedbackService.ReceivedWithAnyArgs(0).SetRatingAsync(default, default);
    }

    [Fact]
    public async Task Favorite_StampedForAnotherUser_IsRefusedAndNothingIsWritten()
    {
        var feedbackService = Substitute.For<IBoulderFeedbackService>();
        var controller = new OfflineActionsController(
            Substitute.For<IAttemptService>(),
            feedbackService,
            Substitute.For<ICommentService>(),
            SignedInAs(Guid.NewGuid()));

        var result = await controller.SetFavorite(new SetFavoriteRequest
        {
            BoulderId = Guid.NewGuid(),
            Favorite = true,
            QueuedForUserId = Guid.NewGuid(),
        });

        AssertHeld(result);
        await feedbackService.ReceivedWithAnyArgs(0).SetFavoriteAsync(default, default);
    }

    [Fact]
    public async Task BoulderCreate_StampedForAnotherUser_IsRefusedAndNothingIsWritten()
    {
        var boulderService = Substitute.For<IBoulderService>();
        var controller = new OfflineBouldersController(boulderService, SignedInAs(Guid.NewGuid()));

        var result = await controller.Create(new CreateBoulderRequest
        {
            Id = Guid.NewGuid(),
            WallId = Guid.NewGuid(),
            Name = "Queued by somebody else",
            QueuedForUserId = Guid.NewGuid(),
        });

        AssertHeld(result);
        await boulderService.ReceivedWithAnyArgs(0).CreateBoulderAsync(default, default!, default, default!);
    }

    [Fact]
    public async Task BoulderRevise_StampedForAnotherUser_IsRefusedAndNothingIsWritten()
    {
        var boulderService = Substitute.For<IBoulderService>();
        var controller = new OfflineBouldersController(boulderService, SignedInAs(Guid.NewGuid()));

        var result = await controller.Revise(Guid.NewGuid(), new ReviseBoulderRequest
        {
            QueuedForUserId = Guid.NewGuid(),
        });

        AssertHeld(result);
        await boulderService.ReceivedWithAnyArgs(0).ReviseBoulderAsync(default, default!);
    }

    #endregion

    #region the guard itself

    [Fact]
    public async Task Ownership_TreatsAnUnstampedEntryAsTheCallersOwn_WithoutResolvingAUser()
    {
        var currentUser = Substitute.For<ICurrentUserService>();

        Assert.True(await OfflineActionOwnership.MatchesCallerAsync(currentUser, null));
        Assert.True(await OfflineActionOwnership.MatchesCallerAsync(currentUser, Guid.Empty));

        // An unstamped entry must not even need an identity lookup: it is the old path verbatim.
        await currentUser.DidNotReceive().GetCurrentUserAsync();
    }

    [Fact]
    public async Task Ownership_MatchesOnlyTheCallersOwnId()
    {
        var me = Guid.NewGuid();
        var currentUser = SignedInAs(me);

        Assert.True(await OfflineActionOwnership.MatchesCallerAsync(currentUser, me));
        Assert.False(await OfflineActionOwnership.MatchesCallerAsync(currentUser, Guid.NewGuid()));
    }

    #endregion

    /// <summary>
    /// 409, and explicitly NOT flagged permanent: the action is perfectly valid, just not for the
    /// person signed in now, so the client keeps it queued instead of discarding it.
    /// </summary>
    private static void AssertHeld(IActionResult result)
    {
        var held = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, held.StatusCode);
        var error = Assert.IsType<OfflineActionError>(held.Value);
        Assert.False(error.Permanent);
        Assert.Equal(OfflineActionOwnership.MismatchMessage, error.Message);
    }

    private static ICurrentUserService SignedInAs(Guid userId)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.GetCurrentUserAsync().Returns(_ => Task.FromResult(new User
        {
            Id = userId,
            Identifier = "signed-in@test",
            DisplayName = "Signed In",
        }));
        return currentUser;
    }

    private static OfflineActionsController ControllerFor(
        IAttemptService attemptService,
        out Guid signedInUserId)
    {
        signedInUserId = Guid.NewGuid();
        return new OfflineActionsController(
            attemptService,
            Substitute.For<IBoulderFeedbackService>(),
            Substitute.For<ICommentService>(),
            SignedInAs(signedInUserId));
    }

    private static OfflineActionsController HarnessController(WallTestHarness h)
    {
        return new OfflineActionsController(
            h.AttemptService,
            h.FeedbackService,
            Substitute.For<ICommentService>(),
            h.CurrentUser);
    }
}
