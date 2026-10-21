// <copyright file="UpdateReviewSafetyTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Refresh.Review;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// The review's safety rails (the adversarial review of 2026-10-21): the decision rows Apply reads are the only truth the
/// review shows, an answer reaches only a current item of this run, no picture guesses a spot or crosses walls, and the
/// kept match is never one an edit during the match made stale.
/// </summary>
public class UpdateReviewSafetyTests
{
    [Fact]
    public async Task AStepByStepSaveAfterAReviewAnswer_IsWhatTheReviewShows()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var news = (await s.ReadAsync()).Items.Where(i => i.Group == ReviewGroup.NewHold).ToList();
        await s.Review.AnswerAsync(s.View.Id, [new($"k-{s.Old[1].Id}", ReviewAnswer.Gone), new(news[1].Key, ReviewAnswer.NotAHold)]);

        // A stale step-by-step tab saves the carry outcome it seeded before the review answers.
        await WallUpdateSessionFixture.Sessions(h).SaveCarryOutcomeAsync(
            h.WallId,
            [new CarryoverDecision(s.Old[0].Id, CarryKind.Carried, null), new CarryoverDecision(s.Old[1].Id, CarryKind.Carried, null)],
            news.Select(n => n.StagedHoldId!.Value).ToList(),
            []);

        var review = await s.ReadAsync();
        Assert.Null(review.Items.Single(i => i.Key == $"k-{s.Old[1].Id}").Answer);
        Assert.Null(review.Items.Single(i => i.Key == news[1].Key).Answer);
        Assert.Equal(2, review.Counts.Open);
    }

    [Fact]
    public async Task AnAnswer_SurvivesTheDetectionMovingFromNewToLeftOut()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var detection = (await s.ReadAsync()).Items.First(i => i.Group == ReviewGroup.NewHold).StagedHoldId!.Value;
        await s.Review.AnswerAsync(s.View.Id, [new($"n-{detection}", ReviewAnswer.NotAHold)]);

        // A 3D re-check now gives the triage a reason: the detection is "left out", and was left out by the person.
        await s.LeaveOutAsync(detection, NewHoldDiscardReason.KnownHoldIn3D);

        var item = (await s.ReadAsync()).Items.Single(i => i.StagedHoldId == detection);
        Assert.Equal((ReviewGroup.LeftOut, ReviewAnswer.LeaveOut, false), (item.Group, item.Answer, item.IsOpen));
    }

    [Fact]
    public async Task AKeyThatIsNoCurrentItem_IsRefused_AndChangesNothing()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var news = (await s.ReadAsync()).Items.Where(i => i.Group == ReviewGroup.NewHold).ToList();

        // A new hold under the left-out key: an undo there used to discard it.
        await Assert.ThrowsAsync<ReviewAnswersStoppedException>(() => s.Review.AnswerAsync(s.View.Id, [new($"d-{news[0].StagedHoldId}", ReviewAnswer.Undo)]));

        // An old hold a card is about is answered on its card, not as "not found" (that bypassed the off-centre rule).
        await AddCardAsync(h, s, s.Old[0].Id);
        await Assert.ThrowsAsync<ReviewAnswersStoppedException>(() => s.Review.AnswerAsync(s.View.Id, [new($"k-{s.Old[0].Id}", ReviewAnswer.Gone)]));

        var review = await s.ReadAsync();
        Assert.Null(review.Items.Single(i => i.Key == news[0].Key).Answer);
        await using var db = h.CreateContext();
        Assert.False((await db.WallUpdateHoldDecisions.SingleAsync(d => d.HoldId == news[0].StagedHoldId)).Discarded);
        Assert.Equal(CarryKind.Carried, (await db.WallUpdateHoldDecisions.SingleAsync(d => d.HoldId == s.Old[0].Id && d.Kind == WallUpdateHoldDecisionKind.Carry)).CarryKind);
        Assert.Empty(await db.WallUpdateReviewAnswers.ToListAsync());
    }

    [Fact]
    public async Task WithoutTheMatch_AKeptHoldHasNoSpot_AndNoGuessedPicture()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var key = $"k-{s.Old[0].Id}";
        await using (var db = h.CreateContext())
        {
            // The processor kept its match; this service's cache is the one an app restart empties.
            Assert.Null(await s.Cache.PeekAsync(db, h.WallId));
        }

        var cold = (await s.ReadAsync()).Items.Single(i => i.Key == key);
        var picture = await s.Review.GetCropAsync(s.View.Id, key, CheckCropView.New, CancellationToken.None);

        Assert.Null(cold.X);
        Assert.Null(cold.Y);
        Assert.Null(picture);
        await WarmAsync(h, s, new Dictionary<Guid, HoldPositionNorm> { [s.Old[0].Id] = new(0.31, 0.32) });
        Assert.Equal(0.31, (await s.ReadAsync()).Items.Single(i => i.Key == key).X);
    }

    [Fact]
    public async Task APictureOfAnotherWallsHold_IsNotServed()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var foreign = await ForeignHoldAsync(h);

        foreach (var key in new[] { $"n-{foreign}", $"d-{foreign}", $"k-{foreign}" })
        {
            foreach (var view in new[] { CheckCropView.Old, CheckCropView.New })
            {
                Assert.Null(await s.Review.GetCropAsync(s.View.Id, key, view, CancellationToken.None));
            }
        }
    }

    [Fact]
    public async Task AnEditDuringTheMatch_MakesTheKeptMatchAMiss()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var cache = new MatchedSessionCache();
        var matches = 0;
        await using var db = h.CreateContext();
        var session = new BigUpdateSession(h.WallId, Guid.NewGuid(), [], [], [], []);

        await cache.GetOrMatchAsync(db, h.WallId, false, async () =>
        {
            matches++;
            await MoveAHoldAsync(h);
            return session;
        });
        Task<BigUpdateSession> Count()
        {
            matches++;
            return Task.FromResult(session);
        }

        await cache.GetOrMatchAsync(db, h.WallId, false, Count);
        await cache.GetOrMatchAsync(db, h.WallId, false, Count);

        Assert.Equal(2, matches);
    }

    [Fact]
    public async Task AReusedFingerprint_GivesWayToACommittedChangeOfTheWall()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var commits = new RecordingFeedCommits();
        var cache = new MatchedSessionCache(commits.Commits);
        var session = new BigUpdateSession(h.WallId, Guid.NewGuid(), [], [], [], []);
        await using var db = h.CreateContext();
        await cache.GetOrMatchAsync(db, h.WallId, false, () => Task.FromResult(session));
        Assert.Same(session, await cache.PeekAsync(db, h.WallId));

        await MoveAHoldAsync(h);
        Assert.Same(session, await cache.PeekAsync(db, h.WallId)); // nothing committed through the feed: still reused

        await using (var edit = TestDb.Create(h.DbContextFactory.ConnectionString, FeedCommitsAfterCommitTests.Interceptor(commits)))
        {
            var hold = await edit.Holds.FirstAsync(x => x.WallId == h.WallId);
            hold.Y += 0.01;
            await edit.SaveChangesAsync();
        }

        Assert.Null(await cache.PeekAsync(db, h.WallId)); // synchronously, at the commit
    }

    /// <summary>The match as the summary keeps it, with the matcher's spots of the old holds on the new photos.</summary>
    private static async Task WarmAsync(WallTestHarness h, ReviewScenario s, Dictionary<Guid, HoldPositionNorm> spots)
    {
        await using var db = h.CreateContext();
        var session = new BigUpdateSession(h.WallId, Guid.NewGuid(), [], [], [], [], CarriedWarpPositions: spots);
        await s.Cache.GetOrMatchAsync(db, h.WallId, false, () => Task.FromResult(session));
    }

    private static async Task AddCardAsync(WallTestHarness h, ReviewScenario s, Guid oldHoldId)
    {
        await using var db = h.CreateContext();
        db.WallUpdateExceptions.Add(new WallUpdateException
        {
            SessionId = s.View.UpdateSessionId!.Value, Kind = UpdateExceptionKind.PossiblyRemoved, OldHoldId = oldHoldId,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> ForeignHoldAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var wall = new Wall { Name = "Other wall", OwnerId = h.Owner.Id, Photo = [1, 2, 3], PhotoContentType = "image/jpeg" };
        var hold = new Hold { WallId = wall.Id, X = 0.5, Y = 0.5, Radius = 0.02 };
        db.Walls.Add(wall);
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold.Id;
    }

    private static async Task MoveAHoldAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var hold = await db.Holds.FirstAsync(x => x.WallId == h.WallId);
        hold.X += 0.01;
        await db.SaveChangesAsync();
    }
}
