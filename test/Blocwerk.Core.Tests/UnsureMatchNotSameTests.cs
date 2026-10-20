// <copyright file="UnsureMatchNotSameTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.LookOver;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.HoldLookFixture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Not the same hold" on an unsure-match card (the API's <see cref="UpdateExceptionAnswer.NotSame"/>), with the full review's
/// unlink semantics: the old hold is carried blind, unconfirmed and marked for review, its boulder stays on it, and the
/// detection goes live as a new hold. An answer for a card that pairs nothing is refused; undo puts the pair back.
/// </summary>
public class UnsureMatchNotSameTests
{
    [Fact]
    public async Task NotTheSameHold_CarriesTheOldHoldMarked_AndTheDetectionIsANewHold()
    {
        using var h = new WallTestHarness();
        var (w, session, card) = await UnsureCardAsync(h);

        await w.Sessions.DecideUpdateExceptionAsync(w.WallId, card, UpdateExceptionAnswer.NotSame, w.SessionId);

        var decision = (await w.Sessions.GetDecisionsAsync(w.WallId)).Carryover.Single(d => d.OldHoldId == w.TargetId);
        Assert.Equal((CarryKind.Carried, (Guid?)null, false), (decision.Kind, decision.NewHoldId, decision.Confirmed));
        Assert.Equal(UpdateExceptionStatus.Removed, (await w.Sessions.GetUpdateExceptionsAsync(w.WallId)).Single().Status);

        await LookOverFixture.PromoteLikeApplyAsync(w, session);

        await using var db = h.CreateContext();
        var onBoulder = (await db.BoulderHolds.SingleAsync(bh => bh.BoulderId == w.BoulderId)).HoldId;
        Assert.NotEqual(w.DetectionId, onBoulder);
        var successor = await db.Holds.SingleAsync(x => x.Id == onBoulder);
        Assert.Equal((3, true), (successor.Generation, successor.NeedsReview));
        Assert.True(await db.Holds.AnyAsync(x => x.Id == w.DetectionId && x.Generation == 3));
        Assert.False(await db.BoulderHolds.AnyAsync(bh => bh.HoldId == w.DetectionId));
    }

    [Fact]
    public async Task NotTheSameHold_Undone_PutsThePairBack()
    {
        using var h = new WallTestHarness();
        var (w, _, card) = await UnsureCardAsync(h);

        await w.Sessions.DecideUpdateExceptionAsync(w.WallId, card, UpdateExceptionAnswer.NotSame, w.SessionId);
        await w.Sessions.DecideUpdateExceptionAsync(w.WallId, card, UpdateExceptionAnswer.Undo, w.SessionId);

        var decision = (await w.Sessions.GetDecisionsAsync(w.WallId)).Carryover.Single(d => d.OldHoldId == w.TargetId);
        Assert.Equal((CarryKind.Carried, w.DetectionId, false), (decision.Kind, decision.NewHoldId, decision.Confirmed));
        Assert.Equal(UpdateExceptionStatus.Pending, (await w.Sessions.GetUpdateExceptionsAsync(w.WallId)).Single().Status);
    }

    [Fact]
    public async Task NotTheSameHold_OnACardThatPairsNothing_IsRefused()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);
        w.Probe.Set(TargetX, TargetY, 0.1);
        await RecordAsync(w, await w.Service.ResumeAsync(w.WallId));
        var card = (await w.Sessions.GetUpdateExceptionsAsync(w.WallId)).Single(c => c.Kind == UpdateExceptionKind.ReplacedByOther);

        var refused = await Assert.ThrowsAsync<UserFacingException>(() =>
            w.Sessions.DecideUpdateExceptionAsync(w.WallId, card.Id, UpdateExceptionAnswer.NotSame, w.SessionId));

        Assert.Equal(WallUpdateSessionService.NotSameRefused, refused.Message);
    }

    /// <summary>The target detected again on its spot, but the photo is unclear about it: carried onto the detection, with a card.</summary>
    private static async Task<(LookWall Wall, BigUpdateSession Session, Guid Card)> UnsureCardAsync(WallTestHarness h)
    {
        var w = await SeedAsync(h, targetDetected: true);
        w.Probe.Set(TargetX, TargetY, 0.4);
        var session = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, session);
        var card = (await w.Sessions.GetUpdateExceptionsAsync(w.WallId)).Single(c => c.Kind == UpdateExceptionKind.LowConfidenceMatch);
        return (w, session, card.Id);
    }
}
