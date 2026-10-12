// <copyright file="HoldLookSessionTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.HoldLookFixture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// After a re-set the matcher still pairs an old hold with whatever was detected on its spot. The look check takes a
/// pair apart when the detection shows another hold, asks about an unclear one, and tells a hold that is gone from one
/// the detector merely missed; the promote then marks the boulders of the holds that are gone, never taking a hold
/// off. What cannot be measured is not judged, and an update where nothing changed stays untouched.
/// </summary>
public class HoldLookSessionTests
{
    [Fact]
    public async Task ReplacedPair_IsNotCarried_GetsACard_AndTheDetectionSurvivesAsNew()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);
        w.Probe.Set(TargetX, TargetY, 0.1);

        var session = await w.Service.ResumeAsync(w.WallId);

        Assert.DoesNotContain(session.Carryover, p => p.OldHoldId == w.TargetId);
        Assert.Equal(Anchors.Length, session.Carryover.Count);
        Assert.Contains(w.TargetId, session.RemovedCandidateHoldIds);
        Assert.Contains(w.DetectionId!.Value, session.NewCenterHoldIds);
        Assert.False(session.SuggestedNewDiscards!.ContainsKey(w.DetectionId.Value));
        var finding = Assert.Single(session.Looks!);
        Assert.Equal((w.TargetId, w.DetectionId.Value, HoldLook.Replaced, (double?)0.1), (finding.OldHoldId, finding.NewHoldId, finding.Look, finding.Score));
        Assert.Equal(HoldMoveOutcome.Replaced, session.NotFoundAgain![w.TargetId]);

        // The quick (API) path: carried in place without a twin, the detection kept as a new hold, one card.
        var quick = await RecordAsync(w, session);
        Assert.Null(quick.Carryover.Single(d => d.OldHoldId == w.TargetId).NewHoldId);
        Assert.Contains(w.DetectionId.Value, quick.AcceptedNewCentreHoldIds);
        var card = Assert.Single(await w.Sessions.GetUpdateExceptionsAsync(w.WallId));
        Assert.Equal((UpdateExceptionKind.ReplacedByOther, w.TargetId, w.DetectionId), (card.Kind, card.OldHoldId, card.StagedHoldId));
    }

    [Fact]
    public async Task ReplacedHold_StaysOnItsBoulder_WhichIsMarkedWithTheReason()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);
        w.Probe.Set(TargetX, TargetY, 0.1);
        var session = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, session);

        await PromoteAsync(w, session);

        await using var db = h.CreateContext();
        var boulder = await db.Boulders.SingleAsync(b => b.Id == w.BoulderId);
        Assert.True(boulder.NeedsReview);
        var membership = await db.BoulderHolds.SingleAsync(bh => bh.BoulderId == w.BoulderId);
        Assert.NotEqual(w.DetectionId, membership.HoldId);
        Assert.Equal(HoldType.Start, membership.Type);
        var kept = await db.Holds.SingleAsync(x => x.Id == membership.HoldId);
        Assert.Equal((3, "Yellow crimp"), (kept.Generation, kept.Name));
        var record = await db.BoulderHoldMoves.SingleAsync(m => m.BoulderId == w.BoulderId);
        Assert.Equal((HoldMoveOutcome.Replaced, w.TargetId, kept.Id, 3), (record.Outcome, record.OldHoldId, record.NewHoldId, record.ToGeneration));

        // The other hold is live as a new hold of its own, without the old hold's name.
        var added = await db.Holds.SingleAsync(x => x.Id == w.DetectionId);
        Assert.Equal((3, null), (added.Generation, added.Name));
        Assert.Equal(Anchors.Length + 2, await db.Holds.CountAsync(x => x.Generation == 3));
    }

    // The detection's spot looks unchanged since the old photo (the second question asked about a low pair): the matcher
    // paired the old hold with something that was always there. Not a replacement. The detection sits on the spot of an
    // old hold that is no longer carried, so the triage keeps it as a new hold instead of dropping it as unchanged.
    [Theory]
    [InlineData(0.2, true)]
    [InlineData(0.5, true)]
    [InlineData(0.6, false)]
    public async Task APairWhoseDetectionWasAlwaysThere_IsNoReplacement_TheOldHoldIsJudgedByItsOwnSpot(double ownSpot, bool gone)
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);
        w.Probe.Set(TargetX, TargetY, 0.1, 0.95, ownSpot, 0.95);

        var session = await w.Service.ResumeAsync(w.WallId);

        Assert.DoesNotContain(session.Carryover, p => p.OldHoldId == w.TargetId);
        Assert.Contains(w.TargetId, session.RemovedCandidateHoldIds);
        Assert.Empty(session.Looks!);
        Assert.False(session.SuggestedNewDiscards!.ContainsKey(w.DetectionId!.Value));
        Assert.Equal(gone ? 1 : 0, session.NotFoundAgain!.Count(n => n.Key == w.TargetId && n.Value == HoldMoveOutcome.NotFound));
        Assert.Equal(gone ? 1 : 0, session.NotFoundAgain!.Count);
        await RecordAsync(w, session);
        Assert.Empty(await w.Sessions.GetUpdateExceptionsAsync(w.WallId));
    }

    [Fact]
    public async Task ReplacedHandPlacedHold_IsNotMergedOntoTheSameDetectionAgain()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true, handPlaced: true);
        w.Probe.Set(TargetX, TargetY, 0.1);

        var session = await w.Service.ResumeAsync(w.WallId);

        Assert.Empty(session.HandPlacedMerges!);
        Assert.DoesNotContain(session.Carryover, p => p.OldHoldId == w.TargetId);
        Assert.Equal(HoldMoveOutcome.Replaced, session.NotFoundAgain![w.TargetId]);
    }

    [Fact]
    public async Task ALowPairOfTheSameKindOfHold_IsOnlyUnsure_ItMayHaveBeenTurned()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);
        w.Probe.Set(TargetX, TargetY, 0.1);
        await using (var db = h.CreateContext())
        {
            var print = new HoldFingerprint { L = 60, A = 10, B = 40, Aspect = 2, Histogram = [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0] }.ToJson();
            await db.Holds.Where(x => x.Id == w.TargetId || x.Id == w.DetectionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.FingerprintJson, print));
        }

        var session = await w.Service.ResumeAsync(w.WallId);

        Assert.Contains(session.Carryover, p => p.OldHoldId == w.TargetId);
        Assert.Equal(HoldLook.Unsure, Assert.Single(session.Looks!).Look);
        Assert.Empty(session.NotFoundAgain!);
    }

    [Fact]
    public async Task UnclearPair_IsCarried_AndAlwaysGetsACard()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);
        w.Probe.Set(TargetX, TargetY, 0.4);

        var session = await w.Service.ResumeAsync(w.WallId);

        // The matcher itself is sure (0.9, above the card bar): the card comes from the look alone.
        var pair = session.Carryover.Single(p => p.OldHoldId == w.TargetId);
        Assert.Equal((w.DetectionId!.Value, 0.9), (pair.NewHoldId, pair.Confidence));
        Assert.Equal(HoldLook.Unsure, Assert.Single(session.Looks!).Look);
        Assert.Empty(session.NotFoundAgain!);
        await RecordAsync(w, session);
        var card = Assert.Single(await w.Sessions.GetUpdateExceptionsAsync(w.WallId));
        Assert.Equal((UpdateExceptionKind.LowConfidenceMatch, w.TargetId, (double?)0.4), (card.Kind, card.OldHoldId, card.PhotoScore));

        await PromoteAsync(w, session);

        await using var db = h.CreateContext();
        Assert.False((await db.Boulders.SingleAsync(b => b.Id == w.BoulderId)).NeedsReview);
        Assert.Equal(w.DetectionId, (await db.BoulderHolds.SingleAsync(bh => bh.BoulderId == w.BoulderId)).HoldId);
        Assert.Empty(await db.BoulderHoldMoves.ToListAsync());
    }

    [Fact]
    public async Task APairThatCannotBeMeasured_IsNotJudged_NoCardNoMark()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);
        w.Probe.Set(TargetX, TargetY, null);

        var session = await w.Service.ResumeAsync(w.WallId);

        Assert.Contains(session.Carryover, p => p.OldHoldId == w.TargetId && p.NewHoldId == w.DetectionId);
        Assert.Empty(session.Looks!);
        Assert.Empty(session.NotFoundAgain!);
        await RecordAsync(w, session);
        Assert.Empty(await w.Sessions.GetUpdateExceptionsAsync(w.WallId));
        await PromoteAsync(w, session);

        await using var db = h.CreateContext();
        Assert.False(await db.Boulders.AnyAsync(b => b.NeedsReview));
        Assert.False(await db.Holds.AnyAsync(x => x.Id == w.DetectionId && x.NeedsReview));
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(0.5)]
    public async Task HoldNotFoundAgain_WhoseSpotLooksGone_StaysOnItsBoulder_WhichIsMarkedWithTheReason(double score)
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: false);
        w.Probe.Set(TargetX, TargetY, score);

        var session = await w.Service.ResumeAsync(w.WallId);
        Assert.Equal(HoldMoveOutcome.NotFound, session.NotFoundAgain![w.TargetId]);
        Assert.Empty(session.Looks!);
        await RecordAsync(w, session);
        await PromoteAsync(w, session);

        await using var db = h.CreateContext();
        Assert.True((await db.Boulders.SingleAsync(b => b.Id == w.BoulderId)).NeedsReview);
        var membership = await db.BoulderHolds.SingleAsync(bh => bh.BoulderId == w.BoulderId);
        var kept = await db.Holds.SingleAsync(x => x.Id == membership.HoldId);
        Assert.Equal((3, TargetX, TargetY), (kept.Generation, kept.X, kept.Y));
        var record = await db.BoulderHoldMoves.SingleAsync();
        Assert.Equal((HoldMoveOutcome.NotFound, w.BoulderId, kept.Id, HoldType.Start), (record.Outcome, record.BoulderId, record.NewHoldId, record.Type));
    }

    [Theory]
    [InlineData(0.9)]
    [InlineData(0.6)]
    [InlineData(0.51)]
    [InlineData(null)]
    public async Task HoldNotFound_WhoseSpotIsUnclearStillThereOrNotMeasurable_IsKeptSilently(double? score)
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: false);
        w.Probe.Set(TargetX, TargetY, score);

        var session = await w.Service.ResumeAsync(w.WallId);
        Assert.Contains(w.TargetId, session.RemovedCandidateHoldIds);
        Assert.Empty(session.NotFoundAgain!);
        await RecordAsync(w, session);
        Assert.Empty(await w.Sessions.GetUpdateExceptionsAsync(w.WallId));
        await PromoteAsync(w, session);

        await using var db = h.CreateContext();
        Assert.False((await db.Boulders.SingleAsync(b => b.Id == w.BoulderId)).NeedsReview);
        Assert.Single(await db.BoulderHolds.Where(bh => bh.BoulderId == w.BoulderId).ToListAsync());
        Assert.Empty(await db.BoulderHoldMoves.ToListAsync());
    }

    [Fact]
    public async Task NothingChanged_NothingIsReplaced_AndNoBoulderIsMarked()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);

        var session = await w.Service.ResumeAsync(w.WallId);

        Assert.Equal(Anchors.Length + 1, session.Carryover.Count);
        Assert.Empty(session.Looks!);
        Assert.Empty(session.NotFoundAgain!);
        Assert.Empty(session.RemovedCandidateHoldIds);
        await RecordAsync(w, session);
        Assert.Empty(await w.Sessions.GetUpdateExceptionsAsync(w.WallId));
        await PromoteAsync(w, session);

        await using var db = h.CreateContext();
        Assert.False(await db.Boulders.AnyAsync(b => b.NeedsReview));
        Assert.Empty(await db.BoulderHoldMoves.ToListAsync());
        Assert.Equal(Anchors.Length + 1, await db.Holds.CountAsync(x => x.Generation == 3));
    }

    [Fact]
    public async Task ABrokenProbe_JudgesNothing()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);
        w.Probe.Throws = true;

        var session = await w.Service.ResumeAsync(w.WallId);

        Assert.Equal(Anchors.Length + 1, session.Carryover.Count);
        Assert.Empty(session.Looks!);
        Assert.Empty(session.NotFoundAgain!);
    }
}
