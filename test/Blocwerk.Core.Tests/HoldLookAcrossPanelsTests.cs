// <copyright file="HoldLookAcrossPanelsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using static Blocwerk.Core.Tests.HoldLookFixture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The look check where more than one panel or more than one run is involved: a hold seen on two panels gets one
/// verdict, a new hold that replaced one is kept on the neighbour panel too, a replacement once asked about stays one,
/// and its card offers only what the promote honours.
/// </summary>
public class HoldLookAcrossPanelsTests
{
    private const double SideX = 0.4;
    private const double SideY = 0.65;

    // The centre's freed detection is a kept new hold when the neighbour is triaged, so the neighbour's detection of the
    // same new hold is kept for the cross-panel link. While the pair stands, the centre photo owns that spot.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheNewHoldThatReplacedOne_IsKeptOnTheNeighbourPanelToo(bool replaced)
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true, neighbour: new LookNeighbour(TargetX, TargetY));
        if (replaced)
        {
            w.Probe.Set(TargetX, TargetY, 0.1);
        }

        var session = await w.Service.ResumeAsync(w.WallId);

        var reason = session.SuggestedNewDiscards!.TryGetValue(w.NeighbourDetectionId!.Value, out var r) ? r : (NewHoldDiscardReason?)null;
        Assert.Equal(replaced ? null : (NewHoldDiscardReason?)NewHoldDiscardReason.SeenOnNeighbourPanel, reason);
        Assert.Equal(replaced, session.NotFoundAgain!.ContainsKey(w.TargetId));
        Assert.False(session.SuggestedNewDiscards.ContainsKey(w.DetectionId!.Value));
    }

    [Theory]
    [InlineData(0.9, 0)]
    [InlineData(0.4, 2)]
    public async Task AHoldLinkedAcrossPanels_TakesTheLeastDrasticVerdictOnBoth(double neighbourScore, int unsure)
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true, neighbour: new LookNeighbour(SideX, SideY, OldHold: true, Linked: true));
        w.Probe.Set(TargetX, TargetY, 0.1);
        w.Probe.Set(SideX, SideY, neighbourScore);

        var session = await w.Service.ResumeAsync(w.WallId);

        // The centre alone would call it replaced; the neighbour panel shows the same physical hold still there.
        Assert.Contains(session.Carryover, p => p.OldHoldId == w.TargetId && p.NewHoldId == w.DetectionId);
        Assert.Contains(session.Carryover, p => p.OldHoldId == w.NeighbourTargetId);
        Assert.Equal(unsure, session.Looks!.Count);
        Assert.All(session.Looks, f => Assert.Equal(HoldLook.Unsure, f.Look));
        Assert.Empty(session.NotFoundAgain!);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AHoldNotFoundOnOnePanel_IsNotGone_WhileALinkedPanelStillShowsIt(bool linked, bool marked)
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: false, neighbour: new LookNeighbour(SideX, SideY, OldHold: true, Linked: linked));
        w.Probe.Set(TargetX, TargetY, 0.1);

        var session = await w.Service.ResumeAsync(w.WallId);

        Assert.Equal(marked, session.NotFoundAgain!.ContainsKey(w.TargetId));
    }

    [Fact]
    public async Task AReplacementOnceAskedAbout_StaysOne_WhateverALaterRunMeasures()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);
        w.Probe.Set(TargetX, TargetY, 0.1);
        var first = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, first);

        w.Probe.Set(TargetX, TargetY, 0.9);
        var second = await w.Service.ResumeAsync(w.WallId);

        Assert.DoesNotContain(second.Carryover, p => p.OldHoldId == w.TargetId);
        Assert.Equal(HoldLook.Replaced, Assert.Single(second.Looks!).Look);
        Assert.Equal(first.NotFoundAgain, second.NotFoundAgain);
        Assert.Contains(w.DetectionId!.Value, second.NewCenterHoldIds);
    }

    [Fact]
    public async Task WithoutACard_TheVerdictFollowsTheMeasurement_AndIsTheSameOnEveryResume()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);
        w.Probe.Set(TargetX, TargetY, 0.1);

        var first = await w.Service.ResumeAsync(w.WallId);
        var second = await w.Service.ResumeAsync(w.WallId);

        Assert.Equal(first.Looks, second.Looks);
        Assert.Equal(first.NotFoundAgain, second.NotFoundAgain);
        Assert.Equal(first.Carryover, second.Carryover);
        w.Probe.Set(TargetX, TargetY, 0.9);
        Assert.Empty((await w.Service.ResumeAsync(w.WallId)).Looks!);
    }

    [Theory]
    [InlineData(UpdateExceptionAnswer.Keep, CarryKind.Carried, true)]
    [InlineData(UpdateExceptionAnswer.Remove, CarryKind.Removed, false)]
    public async Task TheReplacedCard_IsAnsweredLikeAPossiblyRemovedHold(UpdateExceptionAnswer answer, CarryKind kind, bool stillMarked)
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);
        w.Probe.Set(TargetX, TargetY, 0.1);
        var session = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, session);
        var card = Assert.Single(await w.Sessions.GetUpdateExceptionsAsync(w.WallId));

        await w.Sessions.DecideUpdateExceptionAsync(w.WallId, card.Id, answer);

        var decisions = await w.Sessions.GetDecisionsAsync(w.WallId);
        var decision = decisions.Carryover.Single(d => d.OldHoldId == w.TargetId);
        Assert.Equal((kind, (Guid?)null, true), (decision.Kind, decision.NewHoldId, decision.Confirmed));

        // Kept, another hold is still there, so the boulder is still marked; removed, the hold is simply gone.
        var marked = WallBigUpdateService.NotFoundToMark(decisions with { NotFoundAgain = session.NotFoundAgain });
        Assert.Equal(stillMarked, marked.ContainsKey(w.TargetId));
    }

    // A removal on a panel other than the centre is reset to "carried" at the promote (CarryoverScope), so it is not
    // taken here: the card can only be kept, and the hold is removed in the full review.
    [Fact]
    public async Task OnANeighbourPanel_TheReplacedCardCannotBeAnsweredWithRemove()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true, neighbour: new LookNeighbour(SideX, SideY, OldHold: true));
        w.Probe.Set(SideX, SideY, 0.1);
        var session = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, session);
        var card = Assert.Single(await w.Sessions.GetUpdateExceptionsAsync(w.WallId));
        Assert.Equal((UpdateExceptionKind.ReplacedByOther, w.NeighbourTargetId), (card.Kind, card.OldHoldId));

        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Sessions.DecideUpdateExceptionAsync(w.WallId, card.Id, UpdateExceptionAnswer.Remove));
        await w.Sessions.DecideUpdateExceptionAsync(w.WallId, card.Id, UpdateExceptionAnswer.Keep);

        var kept = (await w.Sessions.GetUpdateExceptionsAsync(w.WallId)).Single();
        Assert.Equal(UpdateExceptionStatus.Kept, kept.Status);
    }
}
