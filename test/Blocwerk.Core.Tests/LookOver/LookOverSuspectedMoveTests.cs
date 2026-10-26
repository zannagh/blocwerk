// <copyright file="LookOverSuspectedMoveTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// A hold the 3D placements measured as moved after the update went live, which nobody had confirmed: the look-over asks.
/// "Not moved" changes nothing; "It moved" is a person's confirmation and marks the hold as changed and its boulder for
/// review, without taking the hold off it.
/// </summary>
public class LookOverSuspectedMoveTests
{
    [Fact]
    public async Task NotMoved_SettlesIt_AndMarksNothing()
    {
        using var h = new WallTestHarness();
        var (w, view) = await SuspectedAsync(h);
        Assert.Equal([LookOverAnswer.Settle, LookOverAnswer.ConfirmMove], view.Answers);

        await Service(h).AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);

        var boulder = await BoulderAsync(h, w.BoulderId);
        Assert.Equal((false, w.DetectionId), (boulder.NeedsReview, boulder.BoulderHolds.Single().HoldId));
        Assert.Equal(LookOverFindingStatus.Settled, (await FindingAsync(h, w.WallId)).Status);
    }

    [Fact]
    public async Task ItMoved_MarksTheHoldChanged_AndItsBoulderForReview_KeepingTheHoldOnIt()
    {
        using var h = new WallTestHarness();
        var (w, view) = await SuspectedAsync(h);

        await Service(h).AnswerAsync(w.WallId, view.Id, LookOverAnswer.ConfirmMove, [.. view.Boulders.Select(b => b.Id)]);

        var boulder = await BoulderAsync(h, w.BoulderId);
        Assert.Equal((true, false, w.DetectionId), (boulder.NeedsReview, boulder.IsHistoric, boulder.BoulderHolds.Single().HoldId));
        Assert.True((await HoldAsync(h, w.DetectionId))!.NeedsReview);
        var finding = await FindingAsync(h, w.WallId);
        Assert.Equal((LookOverFindingStatus.Settled, (LookOverAnswer?)LookOverAnswer.ConfirmMove), (finding.Status, finding.Answer));
    }

    /// <summary>An applied update whose one finding is turned into what the 3D re-measure opens for an unconfirmed move.</summary>
    private static async Task<(LookWall Wall, LookOverFindingView View)> SuspectedAsync(WallTestHarness h)
    {
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        await using (var db = h.CreateContext())
        {
            var finding = await db.WallLookOverFindings.SingleAsync(f => f.WallId == w.WallId);
            (finding.Kind, finding.FlaggedHold) = (LookOverFindingKind.SuspectedMove, false);
            await db.SaveChangesAsync();
        }

        var view = Assert.Single(await Service(h).ListAsync(w.WallId));
        Assert.Equal(LookOverFindingKind.SuspectedMove, view.Kind);
        return (w, view);
    }
}
