// <copyright file="PostgresUpdateReviewTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh.Review;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// The one review on a real PostgreSQL: the migration's answers table and its unique key, the answer path's transaction
/// with the session row lock, the summary's compare-and-clear of the request at the database's timestamp precision, and a
/// whole update from staging through the review to Apply.
/// </summary>
[Trait("Db", "Postgres")]
public class PostgresUpdateReviewTests
{
    [PostgresFact]
    public async Task StageReviewApply_EndToEnd_LeavesOnlyWhatNobodyAnswered()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        using var s = await ReviewScenario.PrepareAsync(h);
        Assert.False(s.View.SummaryUpdating);
        var review = await s.ReadAsync();
        var news = review.Items.Where(i => i.Group == ReviewGroup.NewHold).ToList();
        await s.LeaveOutAsync(news[1].StagedHoldId!.Value, NewHoldDiscardReason.KnownHoldIn3D);

        await s.Review.AnswerAsync(s.View.Id, [new($"k-{s.Old[1].Id}", ReviewAnswer.Gone), new(news[0].Key, ReviewAnswer.NotAHold)]);
        var after = await s.ReadAsync();
        var summarized = await s.SummarizeAsync();

        Assert.False(summarized.SummaryUpdating);
        Assert.Equal(1, summarized.Summary!.Removed);
        Assert.Equal(2, after.Counts.Open);
        var done = await s.ApplyAsync();
        Assert.Equal(WallRefreshStatus.Done, done.Status);
        Assert.Equal(after.Counts.Open, done.LookOver!.OpenFindings);
        await using var db = h.CreateContext();
        Assert.False(await db.Holds.AnyAsync(x => x.Id == news[0].StagedHoldId));
        Assert.Equal(LookOverFindingStatus.Open, (await db.WallLookOverFindings.SingleAsync(f => f.Kind == LookOverFindingKind.DiscardedDetection)).Status);
    }

    [PostgresFact]
    public async Task AnswerTwice_KeepsOneRow_AndUndoRemovesIt()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        using var s = await ReviewScenario.PrepareAsync(h);
        var key = $"k-{s.Old[0].Id}";

        await s.Review.AnswerAsync(s.View.Id, [new(key, ReviewAnswer.Gone), new(key, ReviewAnswer.StillThere)]);

        await using (var db = h.CreateContext())
        {
            var row = await db.WallUpdateReviewAnswers.SingleAsync();
            Assert.Equal((s.Old[0].Id, ReviewSubject.NotFound, ReviewAnswer.StillThere, h.Owner.Id), (row.HoldId, row.Subject, row.Answer, row.ByUserId!.Value));
        }

        await s.Review.AnswerAsync(s.View.Id, [new(key, ReviewAnswer.Undo)]);

        await using var after = h.CreateContext();
        Assert.Empty(await after.WallUpdateReviewAnswers.ToListAsync());
    }

    [PostgresFact]
    public async Task LeaveOut_IsSettledAtTheApply()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        using var s = await ReviewScenario.PrepareAsync(h);
        var detection = (await s.ReadAsync()).Items.First(i => i.Group == ReviewGroup.NewHold).StagedHoldId!.Value;
        await s.LeaveOutAsync(detection, NewHoldDiscardReason.OffWallIn3D);

        await s.Review.AnswerAsync(s.View.Id, [new($"d-{detection}", ReviewAnswer.LeaveOut)]);
        await s.SummarizeAsync();
        await s.ApplyAsync();

        await using var db = h.CreateContext();
        var finding = await db.WallLookOverFindings.SingleAsync(f => f.Kind == LookOverFindingKind.DiscardedDetection);
        Assert.Equal((LookOverFindingStatus.Settled, nameof(NewHoldDiscardReason.OffWallIn3D)), (finding.Status, finding.DiscardReason));
    }
}
