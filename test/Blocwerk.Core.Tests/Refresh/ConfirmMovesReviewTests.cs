// <copyright file="ConfirmMovesReviewTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Refresh.Review;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// "Moved?" answers about a carried hold the measurement says may have moved, through the real review and Apply: "it moved"
/// is the very "changed" verdict a person records by hand (the boulder follows the hold and is marked for review), "same
/// hold" marks nothing, and Undo takes back only what "it moved" wrote.
/// </summary>
public class ConfirmMovesReviewTests
{
    [Fact]
    public async Task ItMoved_IsAChangedVerdict_AndApply_RepointsAndMarksTheBoulder()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var twin = await PairAsync(h, s);

        await s.Review.AnswerAsync(s.View.Id, [new($"m-{s.Old[0].Id}", ReviewAnswer.Moved)]);

        var row = await CarryAsync(h, s);
        Assert.Equal((CarryKind.Changed, twin, true), (row.CarryKind, row.PairedHoldId, row.Confirmed));
        await s.SummarizeAsync();
        await s.ApplyAsync();
        await using var db = h.CreateContext();
        Assert.True((await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
        Assert.Equal([twin], await db.BoulderHolds.Where(bh => bh.BoulderId == s.BoulderId).Select(bh => bh.HoldId).ToListAsync());
        Assert.Equal(HoldGenerationLinkKind.Changed, (await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.Old[0].Id)).Kind);
    }

    [Fact]
    public async Task SameHold_MarksNothing_AndApply_RepointsTheBoulderQuietly()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var twin = await PairAsync(h, s);

        await s.Review.AnswerAsync(s.View.Id, [new($"m-{s.Old[0].Id}", ReviewAnswer.SameHold)]);

        var row = await CarryAsync(h, s);
        Assert.Equal((CarryKind.Carried, twin), (row.CarryKind, row.PairedHoldId));
        Assert.Equal(ReviewAnswer.SameHold, (await s.ReadAsync()).Items.Single(i => i.Key == $"m-{s.Old[0].Id}").Answer);
        await s.SummarizeAsync();
        await s.ApplyAsync();
        await using var db = h.CreateContext();
        Assert.False((await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
        Assert.Equal([twin], await db.BoulderHolds.Where(bh => bh.BoulderId == s.BoulderId).Select(bh => bh.HoldId).ToListAsync());
        Assert.Empty(await db.BoulderHoldMoves.ToListAsync());
    }

    [Fact]
    public async Task Undo_TakesBackItMoved_AndForgetsSameHold()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var twin = await PairAsync(h, s);
        var key = $"m-{s.Old[0].Id}";

        await s.Review.AnswerAsync(s.View.Id, [new(key, ReviewAnswer.Moved), new(key, ReviewAnswer.Undo)]);
        var afterMoved = await CarryAsync(h, s);
        await s.Review.AnswerAsync(s.View.Id, [new(key, ReviewAnswer.Moved), new(key, ReviewAnswer.SameHold), new(key, ReviewAnswer.Undo)]);

        Assert.Equal((CarryKind.Carried, twin, false), (afterMoved.CarryKind, afterMoved.PairedHoldId, afterMoved.Confirmed));
        await using var db = h.CreateContext();
        Assert.Empty(await db.WallUpdateReviewAnswers.Where(a => a.Subject == ReviewSubject.Move).ToListAsync());
        var after = await CarryAsync(h, s);
        Assert.Equal((CarryKind.Carried, twin, false), (after.CarryKind, after.PairedHoldId, after.Confirmed));
        Assert.Null((await s.ReadAsync()).Items.Single(i => i.Key == key).Answer);
    }

    [Fact]
    public async Task AHoldNoLongerOnADetection_RefusesAMoveAnswer()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);

        await Assert.ThrowsAsync<ReviewAnswersStoppedException>(() =>
            s.Review.AnswerAsync(s.View.Id, [new($"m-{s.Old[0].Id}", ReviewAnswer.Moved)]));

        Assert.Null((await CarryAsync(h, s)).PairedHoldId);
    }

    /// <summary>
    /// Carries the boulder's old hold onto the first new detection, as a match would (no person confirmed it), and has the
    /// run's summary measure it as possibly moved, as the plan does for a carried pair that shifted.
    /// </summary>
    private static async Task<Guid> PairAsync(WallTestHarness h, ReviewScenario s)
    {
        var detection = (await s.ReadAsync()).Items.First(i => i.Group == ReviewGroup.NewHold).StagedHoldId!.Value;
        await using var db = h.CreateContext();
        var row = await db.WallUpdateHoldDecisions.SingleAsync(d => d.Kind == WallUpdateHoldDecisionKind.Carry && d.HoldId == s.Old[0].Id);
        (row.CarryKind, row.PairedHoldId, row.Confirmed) = (CarryKind.Carried, detection, false);
        var refresh = await db.WallRefreshes.SingleAsync(r => r.Id == s.View.Id);
        var line = new MovedHoldLine(
            s.Old[0].Id, detection, null, 60, HoldMoveSource.TwoD, HoldMoveOutcome.Suspected, "may have moved 6 cm", ["Greetings"]);
        refresh.SummaryJson = RefreshTimeline.Write(RefreshTimeline.Summary(refresh)! with { MovedHolds = [line] });
        await db.SaveChangesAsync();

        var item = (await s.ReadAsync()).Items.Single(i => i.Key == $"m-{s.Old[0].Id}");
        Assert.Equal((ReviewGroup.Moved, ReviewAnswer.SameHold, false), (item.Group, item.Suggested, item.LeavesFinding));
        Assert.Equal([ReviewAnswer.SameHold, ReviewAnswer.Moved], item.Answers);
        Assert.Null(item.Answer);
        return detection;
    }

    private static async Task<WallUpdateHoldDecision> CarryAsync(WallTestHarness h, ReviewScenario s)
    {
        await using var db = h.CreateContext();
        return await db.WallUpdateHoldDecisions.AsNoTracking()
            .SingleAsync(d => d.Kind == WallUpdateHoldDecisionKind.Carry && d.HoldId == s.Old[0].Id);
    }
}
