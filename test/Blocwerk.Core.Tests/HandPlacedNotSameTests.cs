// <copyright file="HandPlacedNotSameTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.LookOver;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.HoldLookFixture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Not the same hold" on a hand-placed merge: like on an unsure match the old hold goes live carried unconfirmed and marked
/// for review, and it is then no merge for the promote (no detection outline adopted, measured as any carried pair). Undo
/// puts the pair back, the detection's own decision included (a neighbour's "removed" too).
/// </summary>
public class HandPlacedNotSameTests
{
    [Fact]
    public async Task NotTheSameHold_IsNoMergeAnyMore_AndGoesLiveUnconfirmedAndMarked()
    {
        using var h = new WallTestHarness();
        var (w, session, card) = await MergeCardAsync(h);
        Assert.Equal([w.TargetId], PromotePayload.For(await w.Sessions.GetDecisionsAsync(w.WallId), session).HandPlacedMergeOldIds);

        await w.Sessions.DecideUpdateExceptionAsync(w.WallId, card, UpdateExceptionAnswer.NotSame, w.SessionId);

        var decisions = await w.Sessions.GetDecisionsAsync(w.WallId);
        var row = decisions.Carryover.Single(d => d.OldHoldId == w.TargetId);
        Assert.Equal(((Guid?)null, false), (row.NewHoldId, row.Confirmed));
        Assert.Empty(PromotePayload.For(decisions, session).HandPlacedMergeOldIds!);
        var cards = await w.Sessions.GetUpdateExceptionsAsync(w.WallId);
        Assert.Equal(UpdateExceptionStatus.NotSame, cards.Single().Status);
        Assert.Contains(w.TargetId, RefreshDecisions.ReviewOldHoldIds(cards));

        await LookOverFixture.PromoteLikeApplyAsync(w, session);

        await using var db = h.CreateContext();
        var onBoulder = (await db.BoulderHolds.SingleAsync(bh => bh.BoulderId == w.BoulderId)).HoldId;
        Assert.NotEqual(w.DetectionId, onBoulder);
        Assert.True((await db.Holds.SingleAsync(x => x.Id == onBoulder)).NeedsReview);
        Assert.True(await db.Holds.AnyAsync(x => x.Id == w.DetectionId && x.Generation == 3));
    }

    [Fact]
    public async Task DontMerge_StaysConfirmed_AndUnmarked()
    {
        using var h = new WallTestHarness();
        var (w, _, card) = await MergeCardAsync(h);

        await w.Sessions.DecideUpdateExceptionAsync(w.WallId, card, UpdateExceptionAnswer.Remove, w.SessionId);

        Assert.True((await w.Sessions.GetDecisionsAsync(w.WallId)).Carryover.Single(d => d.OldHoldId == w.TargetId).Confirmed);
        Assert.DoesNotContain(w.TargetId, RefreshDecisions.ReviewOldHoldIds(await w.Sessions.GetUpdateExceptionsAsync(w.WallId)));
    }

    [Fact]
    public async Task Undo_AfterNotTheSameHold_PutsTheDecisionsBackAsTheyWere()
    {
        using var h = new WallTestHarness();
        var (w, session, card) = await MergeCardAsync(h);
        var before = await w.Sessions.GetDecisionsAsync(w.WallId);

        await w.Sessions.DecideUpdateExceptionAsync(w.WallId, card, UpdateExceptionAnswer.NotSame, w.SessionId);
        Assert.Contains(w.DetectionId!.Value, (await w.Sessions.GetDecisionsAsync(w.WallId)).AcceptedNewCenterHoldIds);
        await w.Sessions.DecideUpdateExceptionAsync(w.WallId, card, UpdateExceptionAnswer.Undo, w.SessionId);

        var after = await w.Sessions.GetDecisionsAsync(w.WallId);
        Assert.Equal(before.AcceptedNewCenterHoldIds.Order(), after.AcceptedNewCenterHoldIds.Order());
        Assert.Equal(before.RemovedNewCenterHoldIds.Order(), after.RemovedNewCenterHoldIds.Order());
        Assert.Equal(w.DetectionId, after.Carryover.Single(d => d.OldHoldId == w.TargetId).NewHoldId);
        Assert.Equal([w.TargetId], PromotePayload.For(after, session).HandPlacedMergeOldIds);
    }

    [Fact]
    public async Task Undo_AfterNotTheSameHold_OnANeighbour_RestoresItsRemovedDecision()
    {
        using var h = new WallTestHarness();
        var (w, _, card) = await MergeCardAsync(h);
        var neighbourPanel = await MoveDetectionToANeighbourAsync(h, w);

        await w.Sessions.DecideUpdateExceptionAsync(w.WallId, card, UpdateExceptionAnswer.NotSame, w.SessionId);
        Assert.False(await RemovedAsync(h, w));
        await w.Sessions.DecideUpdateExceptionAsync(w.WallId, card, UpdateExceptionAnswer.Undo, w.SessionId);

        Assert.True(await RemovedAsync(h, w));
        var removed = (await w.Sessions.GetDecisionsAsync(w.WallId)).Neighbours.Single(n => n.PanelId == neighbourPanel).RemovedNeighbourHoldIds;
        Assert.Equal([w.DetectionId!.Value], removed);
    }

    [Fact]
    public void OnlyMergesTheDecisionsStillMake_AreMerges_InTheFullReviewsPayloadToo()
    {
        var (merged, unlinked, repaired, removed) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var detection = new Dictionary<Guid, Guid> { [merged] = Guid.NewGuid(), [unlinked] = Guid.NewGuid(), [repaired] = Guid.NewGuid(), [removed] = Guid.NewGuid() };
        var session = new BigUpdateSession(Guid.NewGuid(), Guid.NewGuid(), [], [], [], [], HandPlacedMerges: detection.Select(d => new HandPlacedMerge(d.Key, d.Value)).ToList());
        var decisions = new BigUpdateConfirmation(
            [
                new CarryoverDecision(merged, CarryKind.Carried, detection[merged]),
                new CarryoverDecision(unlinked, CarryKind.Carried, null),
                new CarryoverDecision(repaired, CarryKind.Carried, Guid.NewGuid()),
                new CarryoverDecision(removed, CarryKind.Removed, detection[removed]),
            ],
            [], [], []);

        Assert.Equal([merged], PromotePayload.For(decisions, session).HandPlacedMergeOldIds);
    }

    /// <summary>The target placed by hand and detected again on its spot: the update merges it onto the detection, with a card.</summary>
    private static async Task<(LookWall Wall, BigUpdateSession Session, Guid Card)> MergeCardAsync(WallTestHarness h)
    {
        var w = await SeedAsync(h, targetDetected: true, handPlaced: true);
        await using (var db = h.CreateContext())
        {
            // Beyond the matcher's reach but well within the hand-placed circle's: the matcher leaves both unpaired.
            await db.Holds.Where(x => x.Id == w.TargetId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Radius, 0.04));
            await db.Holds.Where(x => x.Id == w.DetectionId).ExecuteUpdateAsync(u => u.SetProperty(x => x.X, TargetX + 0.015).SetProperty(x => x.Radius, 0.05));
        }

        var session = await w.Service.ResumeAsync(w.WallId);
        Assert.Contains(session.HandPlacedMerges!, m => m.OldHoldId == w.TargetId && m.NewHoldId == w.DetectionId);
        await RecordAsync(w, session);
        var card = (await w.Sessions.GetUpdateExceptionsAsync(w.WallId)).Single(c => c.Kind == UpdateExceptionKind.MatchedToHandPlaced);
        return (w, session, card.Id);
    }

    /// <summary>
    /// The detection as a neighbour panel's, which the triage suggested to drop (so the defaults recorded it removed): the
    /// state a neighbour's hand-placed merge is in.
    /// </summary>
    private static async Task<Guid> MoveDetectionToANeighbourAsync(WallTestHarness h, LookWall w)
    {
        await using var db = h.CreateContext();
        var panel = new WallPanel { WallId = w.WallId, Col = 1, Row = 0, StagedPhoto = CaptureScenario.TinyJpeg(9), StagedPhotoContentType = "image/jpeg", Generation = 3 };
        db.WallPanels.Add(panel);
        var detection = await db.Holds.SingleAsync(x => x.Id == w.DetectionId);
        (detection.WallPanelId, detection.DiscardReason) = (panel.Id, nameof(NewHoldDiscardReason.SeenOnNeighbourPanel));
        db.WallUpdateHoldDecisions.RemoveRange(db.WallUpdateHoldDecisions.Where(d => d.Kind == WallUpdateHoldDecisionKind.NewCentreHold && d.HoldId == detection.Id));
        db.WallUpdateNeighbourDecisions.Add(new WallUpdateNeighbourDecision
        {
            SessionId = w.SessionId, Kind = WallUpdateNeighbourDecisionKind.Removed, PanelId = panel.Id, HoldId = detection.Id,
        });
        await db.SaveChangesAsync();
        return panel.Id;
    }

    private static async Task<bool> RemovedAsync(WallTestHarness h, LookWall w)
    {
        await using var db = h.CreateContext();
        return await db.WallUpdateNeighbourDecisions.AnyAsync(d => d.SessionId == w.SessionId && d.Kind == WallUpdateNeighbourDecisionKind.Removed && d.HoldId == w.DetectionId);
    }
}
