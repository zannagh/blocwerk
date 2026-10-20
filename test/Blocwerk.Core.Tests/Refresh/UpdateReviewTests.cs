// <copyright file="UpdateReviewTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh.Review;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// The one review of "Update wall": it holds every hold the look-over would ask about after Apply, its counts are the
/// look-over's, an answer is written as the decision Apply reads (and leaves nothing for the look-over), and a refused
/// answer stops the rest without losing those before it.
/// </summary>
public class UpdateReviewTests
{
    [Fact]
    public async Task TheReview_HoldsEveryLaterFinding_AndItsOpenCountIsTheLookOvers()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);

        var review = await s.ReadAsync();

        Assert.Equal(2, review.Items.Count(i => i.Group == ReviewGroup.NotFound));
        Assert.Equal(2, review.Items.Count(i => i.Group == ReviewGroup.NewHold));
        Assert.Equal(["Greetings"], review.Items.Single(i => i.OldHoldId == s.Old[0].Id).Boulders);
        Assert.Equal(2, review.Counts.Open);
        Assert.Equal(4, review.Counts.ToCheck);

        var done = await s.ApplyAsync();

        Assert.Equal(WallRefreshStatus.Done, done.Status);
        Assert.Equal(review.Counts.Open, done.LookOver!.OpenFindings);
    }

    [Fact]
    public async Task AnsweredInTheReview_IsWhatApplyDoes_AndNothingIsLeftForTheLookOver()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var before = s.View.Summary!.DecisionsVersion;
        var news = (await s.ReadAsync()).Items.Where(i => i.Group == ReviewGroup.NewHold).ToList();

        var after = await s.Review.AnswerAsync(s.View.Id,
        [
            new($"k-{s.Old[0].Id}", ReviewAnswer.StillThere),
            new($"k-{s.Old[1].Id}", ReviewAnswer.Gone),
            new(news[0].Key, ReviewAnswer.KeepNew),
            new(news[1].Key, ReviewAnswer.NotAHold),
        ]);

        Assert.Equal((0, 4, 0), (after.Counts.Open, after.Counts.Answered, after.Counts.ToCheck));
        Assert.True(after.SummaryUpdating);
        var summarized = await s.SummarizeAsync();
        Assert.NotEqual(before, summarized.Summary!.DecisionsVersion);
        Assert.Equal(1, summarized.Summary.Removed);

        var done = await s.ApplyAsync();

        Assert.Equal(WallRefreshStatus.Done, done.Status);
        Assert.Null(done.LookOver);
        await using var db = h.CreateContext();
        var live = await db.Holds.Where(x => x.Generation == 1).Select(x => x.Id).ToListAsync();
        var successors = await db.HoldGenerationLinks.Where(l => l.ToGeneration == 1).Select(l => new { l.OldHoldId, l.NewHoldId }).ToListAsync();
        Assert.Contains(successors, l => l.OldHoldId == s.Old[0].Id && l.NewHoldId != null && live.Contains(l.NewHoldId.Value));
        Assert.DoesNotContain(successors, l => l.OldHoldId == s.Old[1].Id && l.NewHoldId != null);
        Assert.Contains(news[0].StagedHoldId!.Value, live);
        Assert.DoesNotContain(news[1].StagedHoldId!.Value, live);
        Assert.Empty(await db.WallLookOverFindings.ToListAsync());
        Assert.False((await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).IsHistoric);
    }

    [Fact]
    public async Task Undo_PutsTheDefaultBack_AndForgetsTheAnswer()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var key = $"k-{s.Old[1].Id}";
        await s.Review.AnswerAsync(s.View.Id, [new(key, ReviewAnswer.Gone)]);
        await s.SummarizeAsync();

        var undone = await s.Review.AnswerAsync(s.View.Id, [new(key, ReviewAnswer.Undo)]);
        var summary = (await s.SummarizeAsync()).Summary!;

        Assert.Null(undone.Items.Single(i => i.Key == key).Answer);
        Assert.Equal(0, summary.Removed);
        await using var db = h.CreateContext();
        Assert.Empty(await db.WallUpdateReviewAnswers.ToListAsync());
        var row = await db.WallUpdateHoldDecisions.SingleAsync(d => d.HoldId == s.Old[1].Id && d.Kind == WallUpdateHoldDecisionKind.Carry);
        Assert.Equal((CarryKind.Carried, false), (row.CarryKind, row.Confirmed));
    }

    [Fact]
    public async Task AcceptSuggested_AnswersTheGroup_OfOnePanel_InOneCall()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var panel = (await s.ReadAsync()).Panels.Single().PanelId;

        var (answered, after) = await s.Review.AcceptSuggestedAsync(s.View.Id, ReviewGroup.NotFound, panel);

        Assert.Equal(2, answered);
        Assert.All(after.Items.Where(i => i.Group == ReviewGroup.NotFound), i => Assert.Equal(ReviewAnswer.StillThere, i.Answer));
        Assert.Equal("Owner", after.Items.First(i => i.Group == ReviewGroup.NotFound).AnsweredBy);
        Assert.Equal(0, after.Counts.Open);
        Assert.Equal(2, after.Counts.ToCheck);
        Assert.Equal(0, (await s.Review.AcceptSuggestedAsync(s.View.Id, ReviewGroup.NotFound)).Answered);
    }

    [Fact]
    public async Task ALeftOutDetection_IsAskedAbout_AndAnswerLeaveOut_IsSettledAtTheApply()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var news = (await s.ReadAsync()).Items.Where(i => i.Group == ReviewGroup.NewHold).ToList();

        // As the 3D model would: a pass without it keeps the reasons, so Apply still knows them.
        await s.LeaveOutAsync(news[0].StagedHoldId!.Value, NewHoldDiscardReason.KnownHoldIn3D);
        await s.LeaveOutAsync(news[1].StagedHoldId!.Value, NewHoldDiscardReason.OffWallIn3D);

        var review = await s.ReadAsync();
        var leftOut = review.Items.Where(i => i.Group == ReviewGroup.LeftOut).ToList();
        Assert.Equal(2, leftOut.Count);
        Assert.All(leftOut, i => Assert.True(i.LeavesFinding, "a confident detection on bare wall is likely a missed hold"));
        Assert.Equal(4, review.Counts.Open);

        await s.Review.AnswerAsync(s.View.Id, [new(leftOut[0].Key, ReviewAnswer.LeaveOut)]);
        await s.SummarizeAsync();
        await s.ApplyAsync();

        await using var db = h.CreateContext();
        var findings = await db.WallLookOverFindings.Where(f => f.Kind == LookOverFindingKind.DiscardedDetection).ToListAsync();
        Assert.Equal(2, findings.Count);
        var settled = findings.Single(f => f.Status == LookOverFindingStatus.Settled);
        Assert.Equal((leftOut[0].X!.Value, LookOverAnswer.Settle, h.Owner.Id), (settled.X, settled.Answer!.Value, settled.AnsweredByUserId!.Value));
        Assert.Single(findings, f => f.Status == LookOverFindingStatus.Open);
    }

    [Fact]
    public async Task AddAsHold_BringsALeftOutDetectionLive()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var detection = (await s.ReadAsync()).Items.First(i => i.Group == ReviewGroup.NewHold).StagedHoldId!.Value;
        await s.LeaveOutAsync(detection, NewHoldDiscardReason.OnMarker);

        var after = await s.Review.AnswerAsync(s.View.Id, [new($"d-{detection}", ReviewAnswer.AddAsHold)]);
        await s.SummarizeAsync();
        await s.ApplyAsync();

        Assert.Equal(ReviewAnswer.AddAsHold, after.Items.Single(i => i.StagedHoldId == detection).Answer);
        await using var db = h.CreateContext();
        Assert.True(await db.Holds.AnyAsync(x => x.Id == detection && x.Generation == 1));
        Assert.Empty(await db.WallLookOverFindings.Where(f => f.Kind == LookOverFindingKind.DiscardedDetection).ToListAsync());
    }

    [Fact]
    public async Task ARefusedAnswer_StopsTheRest_AndSaysWhichOne()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var keep = $"k-{s.Old[0].Id}";
        var wrong = $"k-{s.Old[1].Id}";

        var stopped = await Assert.ThrowsAsync<ReviewAnswersStoppedException>(() => s.Review.AnswerAsync(s.View.Id,
            [new(keep, ReviewAnswer.StillThere), new(wrong, ReviewAnswer.AddAsHold), new($"k-{Guid.NewGuid()}", ReviewAnswer.Gone)]));

        Assert.Equal((1, wrong), (stopped.Answered, stopped.FailedKey));
        var review = await s.ReadAsync();
        Assert.Equal(ReviewAnswer.StillThere, review.Items.Single(i => i.Key == keep).Answer);
        Assert.Null(review.Items.Single(i => i.Key == wrong).Answer);
        await Assert.ThrowsAnyAsync<UserFacingException>(() => s.Review.AnswerAsync(s.View.Id, [new("x-nonsense", ReviewAnswer.Keep)]));
    }

    [Fact]
    public async Task AfterTheUpdateWasDiscarded_TheReviewIsGone_AndAnswersAreRefused()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);

        await s.Refresh.Service.DiscardAsync(s.View.Id);

        Assert.Null(await s.Review.GetAsync(s.View.Id));
        await Assert.ThrowsAsync<UserFacingException>(() => s.Review.AnswerAsync(s.View.Id, [new($"k-{s.Old[0].Id}", ReviewAnswer.StillThere)]));
    }

    [Fact]
    public async Task AnAnswer_IsSummarizedWithoutMatchingAgain()
    {
        using var h = new WallTestHarness();
        var resumes = 0;
        using var s = await ReviewScenario.PrepareAsync(h);
        s.Refresh.Decorate = a => a with { BigUpdate = new HookedBigUpdate(a.BigUpdate, () => Task.FromResult(resumes++)) };

        await s.Review.AnswerAsync(s.View.Id, [new($"k-{s.Old[0].Id}", ReviewAnswer.StillThere)]);
        await s.SummarizeAsync();
        await s.Review.AnswerAsync(s.View.Id, [new($"k-{s.Old[1].Id}", ReviewAnswer.StillThere)]);
        var view = await s.SummarizeAsync();

        Assert.Equal(0, resumes);
        Assert.False(view.SummaryUpdating);
        Assert.Equal(WallRefreshStatus.Done, (await s.ApplyAsync()).Status);
        Assert.Equal(1, resumes);
    }
}
