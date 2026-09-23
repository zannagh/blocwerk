// <copyright file="WallUpdateShapeStepTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The wall update's optional shape step: it is optional, it resumes, it never overwrites hand-drawn
/// outlines, its verdicts reach the holds on promote and only on promote, and a stale tab is refused.
/// </summary>
public class WallUpdateShapeStepTests
{
    [Fact]
    public async Task NeverRun_PromoteKeepsEveryShapeAsItIs()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        var ids = await f.StageAsync(ShapeStepFixture.AutoHold(0.6, ShapeStepFixture.Triangle(0.01), HoldOutlineSource.AutoContour));

        await f.PromoteAsync(ids);

        var hold = await LoadAsync(h, ids[0]);
        Assert.Equal(3, hold.ShapePoints!.Count);
        Assert.Null(hold.OutlineConfidence);
    }

    [Fact]
    public async Task Skipped_EvenAfterDecisions_PromoteAppliesNothing()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        var ids = await f.StageAsync(ShapeStepFixture.AutoHold(0.6));
        await f.RecogniseAsync();
        await f.Service.DecideAsync(h.WallId, [new ShapeDecisionRequest(ids[0], ShapeReviewDecision.Accepted)], f.SessionId);

        var status = await f.Service.SkipAsync(h.WallId, f.SessionId);
        await f.PromoteAsync(ids);

        Assert.Equal(ShapeRecognitionStatus.Skipped, status.Status);
        Assert.Null((await LoadAsync(h, ids[0])).ShapePoints);
    }

    [Fact]
    public async Task Run_RecordsOneProposalPerHold_LowestConfidenceFirst_AndTouchesNoHold()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        var ids = await f.StageAsync(ShapeStepFixture.AutoHold(0.9), ShapeStepFixture.AutoHold(0.2), ShapeStepFixture.AutoHold(0.5));

        var status = await f.RecogniseAsync();
        var proposals = await f.Service.GetProposalsAsync(h.WallId);

        Assert.Equal(ShapeRecognitionStatus.Completed, status.Status);
        Assert.Equal((3, 3), (status.Total, status.Done));
        Assert.Equal([ids[1], ids[2], ids[0]], proposals.Select(p => p.HoldId));
        Assert.Equal(HoldOutlineMethod.CircleFallback, proposals[0].Method);
        Assert.Null(proposals[0].Shape);
        Assert.True(proposals[0].Confidence <= 0.2);
        Assert.Single(await f.Service.GetProposalsAsync(h.WallId, belowConfidence: 0.3));
        foreach (var id in ids)
        {
            Assert.Null((await LoadAsync(h, id)).ShapePoints);
        }
    }

    [Fact]
    public async Task HandDrawnShapes_AreSkipped_UnlessOverwriteIsAsked()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        var ids = await f.StageAsync(
            ShapeStepFixture.AutoHold(0.6, ShapeStepFixture.Triangle(0.01), HoldOutlineSource.Manual),
            ShapeStepFixture.AutoHold(0.7));

        var status = await f.RecogniseAsync();
        Assert.Equal(1, status.SkippedManual);
        Assert.DoesNotContain(await f.Service.GetProposalsAsync(h.WallId), p => p.HoldId == ids[0]);

        await f.Service.AcceptAboveAsync(h.WallId, 0, f.SessionId);
        await f.PromoteAsync(ids);
        var manual = await LoadAsync(h, ids[0]);
        Assert.Equal(HoldOutlineSource.Manual, manual.OutlineSource);
        Assert.Equal(-0.01, manual.ShapePoints![0].Dy, 6);
    }

    [Fact]
    public async Task OverwriteManual_ProposesForHandDrawnShapesToo()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        var ids = await f.StageAsync(ShapeStepFixture.AutoHold(0.6, ShapeStepFixture.Triangle(0.01), HoldOutlineSource.Manual));

        var status = await f.RecogniseAsync(new ShapeRecognitionOptions(OverwriteManual: true));

        Assert.Equal(0, status.SkippedManual);
        var proposal = Assert.Single(await f.Service.GetProposalsAsync(h.WallId));
        Assert.Equal(ids[0], proposal.HoldId);
        Assert.Equal(3, proposal.PreviousShape!.Count);
    }

    [Fact]
    public async Task Verdicts_ReachTheHolds_OnPromote_AndOnlyThen()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        var ids = await f.StageAsync(
            ShapeStepFixture.AutoHold(0.9),
            ShapeStepFixture.AutoHold(0.5, ShapeStepFixture.Triangle(0.01), HoldOutlineSource.AutoContour),
            ShapeStepFixture.AutoHold(0.7));
        await f.RecogniseAsync();
        var adjusted = ShapeStepFixture.Triangle(0.03);
        await f.Service.DecideAsync(
            h.WallId,
            [
                new ShapeDecisionRequest(ids[0], ShapeReviewDecision.Accepted),
                new ShapeDecisionRequest(ids[1], ShapeReviewDecision.Circle),
                new ShapeDecisionRequest(ids[2], ShapeReviewDecision.Adjusted, adjusted),
            ],
            f.SessionId);

        Assert.Null((await LoadAsync(h, ids[0])).ShapePoints);
        Assert.Equal(3, (await LoadAsync(h, ids[1])).ShapePoints!.Count);

        await f.PromoteAsync(ids);

        var accepted = await LoadAsync(h, ids[0]);
        Assert.Equal(4, accepted.ShapePoints!.Count);
        Assert.Equal(HoldOutlineSource.AutoContour, accepted.OutlineSource);
        Assert.Equal(0.75, accepted.OutlineConfidence!.Value, 3);
        var circle = await LoadAsync(h, ids[1]);
        Assert.Null(circle.ShapePoints);
        Assert.Equal(HoldOutlineSource.Manual, circle.OutlineSource);
        var own = await LoadAsync(h, ids[2]);
        Assert.Equal(0.03, own.ShapePoints![1].Dx, 6);
        Assert.Equal(HoldOutlineSource.Manual, own.OutlineSource);
        Assert.Null(own.OutlineConfidence);
    }

    [Fact]
    public async Task AcceptAbove_AcceptsOnlyPendingOutlinesAtOrAboveTheThreshold()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        var ids = await f.StageAsync(ShapeStepFixture.AutoHold(0.9), ShapeStepFixture.AutoHold(0.4), ShapeStepFixture.AutoHold(0.2));
        await f.RecogniseAsync();
        await f.Service.DecideAsync(h.WallId, [new ShapeDecisionRequest(ids[0], ShapeReviewDecision.Circle)], f.SessionId);

        var accepted = await f.Service.AcceptAboveAsync(h.WallId, 0.45, f.SessionId);

        Assert.Equal(1, accepted);
        var byId = (await f.Service.GetProposalsAsync(h.WallId)).ToDictionary(p => p.HoldId);
        Assert.Equal(ShapeReviewDecision.Circle, byId[ids[0]].Decision);
        Assert.Equal(ShapeReviewDecision.Accepted, byId[ids[1]].Decision);
        Assert.Equal(ShapeReviewDecision.Pending, byId[ids[2]].Decision);
    }

    [Fact]
    public async Task InterruptedRun_IsReported_AndResumesWithoutRedoingFinishedHolds()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        var ids = await f.StageAsync(ShapeStepFixture.AutoHold(0.6), ShapeStepFixture.AutoHold(0.8));
        var earlier = await SimulateInterruptedRunAsync(h, f, ids[0]);

        var interrupted = await f.Service.GetStatusAsync(h.WallId);
        Assert.True(interrupted.Interrupted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.PromoteAsync(ids));

        var status = await f.RecogniseAsync();

        Assert.Equal(ShapeRecognitionStatus.Completed, status.Status);
        Assert.Equal((2, 2), (status.Total, status.Done));
        await using var db = h.CreateContext();
        Assert.Equal(earlier, (await db.WallUpdateShapeProposals.SingleAsync(p => p.HoldId == ids[0])).Id);
        Assert.Equal(ShapeReviewDecision.Accepted, (await db.WallUpdateShapeProposals.SingleAsync(p => p.HoldId == ids[0])).Decision);
    }

    [Fact]
    public async Task StaleTab_IsRefused_ForEveryWrite()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        var ids = await f.StageAsync(ShapeStepFixture.AutoHold(0.6));
        await f.RecogniseAsync();
        var stale = Guid.NewGuid();

        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(
            () => f.Service.StartRecognitionAsync(h.WallId, new ShapeRecognitionOptions(Rerun: true), stale));
        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(
            () => f.Service.DecideAsync(h.WallId, [new ShapeDecisionRequest(ids[0], ShapeReviewDecision.Accepted)], stale));
        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(() => f.Service.AcceptAboveAsync(h.WallId, 0, stale));
        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(() => f.Service.SkipAsync(h.WallId, stale));
        Assert.Equal(ShapeReviewDecision.Pending, Assert.Single(await f.Service.GetProposalsAsync(h.WallId)).Decision);
    }

    [Fact]
    public async Task Review_BeforeTheRunCompleted_IsRefused()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        var ids = await f.StageAsync(ShapeStepFixture.AutoHold(0.6));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Service.DecideAsync(h.WallId, [new ShapeDecisionRequest(ids[0], ShapeReviewDecision.Accepted)], f.SessionId));
    }

    [Fact]
    public async Task NonAdmins_AndKioskSessions_AreRefused()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        await f.StageAsync(ShapeStepFixture.AutoHold(0.6));

        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.GetStatusAsync(h.WallId));

        h.ActingUser = h.Owner;
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(h.WallId);
        var onTablet = new ShapeStepFixture(h, kiosk);
        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => onTablet.Service.StartRecognitionAsync(h.WallId, new ShapeRecognitionOptions()));
    }

    /// <summary>One hold already proposed (and accepted), the run marked Running with nothing alive.</summary>
    [Fact]
    public async Task Decide_RefusesAnOversizedBatch_BeforeTouchingTheDatabase()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        var ids = await f.StageAsync(ShapeStepFixture.AutoHold(0.6));
        await f.RecogniseAsync();
        var flood = Enumerable.Repeat(new ShapeDecisionRequest(ids[0], ShapeReviewDecision.Accepted), WallUpdateShapeService.MaxDecisionsPerWrite + 1).ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.DecideAsync(h.WallId, flood, f.SessionId));
        Assert.Equal(ShapeReviewDecision.Pending, (await f.Service.GetProposalsAsync(h.WallId)).Single().Decision);
    }

    private static async Task<Guid> SimulateInterruptedRunAsync(WallTestHarness h, ShapeStepFixture f, Guid doneHoldId)
    {
        await using var db = h.CreateContext();
        var session = await db.WallUpdateSessions.SingleAsync(s => s.Id == f.SessionId);
        session.ShapeStatus = ShapeRecognitionStatus.Running;
        var row = new WallUpdateShapeProposal
        {
            SessionId = f.SessionId,
            HoldId = doneHoldId,
            PanelId = f.PanelId,
            AnchorX = 0.6,
            AnchorY = 0.5,
            Confidence = 0.5,
            ShapeJson = ShapeJson.Write(ShapeStepFixture.Triangle(0.01)),
            Decision = ShapeReviewDecision.Accepted,
        };
        db.WallUpdateShapeProposals.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static async Task<Hold> LoadAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return await db.Holds.AsNoTracking().SingleAsync(x => x.Id == id);
    }
}
