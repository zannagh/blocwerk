// <copyright file="HoldMoveFollowUpTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// 3D before the confirm decision (provisional placements of the staged holds feed the plan and its version) and 3D
/// after the placement run (a later measurement marks boulders for review without silently changing them).
/// </summary>
public class HoldMoveFollowUpTests
{
    private sealed class FakePlacer(Dictionary<Guid, StagedPlacement> placements) : IStagedHoldPlacer
    {
        public Task<IReadOnlyDictionary<Guid, StagedPlacement>> PlaceAsync(IReadOnlyList<(Hold Old, Hold Twin)> pairs) =>
            Task.FromResult<IReadOnlyDictionary<Guid, StagedPlacement>>(placements);
    }

    private static WallBigUpdateService Service(WallTestHarness h, IStagedHoldPlacer? placer) =>
        new(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, new PositionHoldMatcher(), NullLogger<WallBigUpdateService>.Instance, moveOptions: HoldMovePromoteTests.Loose, stagedPlacer: placer);

    [Fact]
    public async Task ProvisionalPlacements_GiveThePlanItsDistanceIn3D_BeforeAnythingIsPromoted()
    {
        using var h = new WallTestHarness();
        var s = await HoldMovePromoteTests.SeedAsync(h, movedToA: 1000, placeStaged: false);
        var placer = new FakePlacer(HoldMovePromoteTests.Provisional(s, 1340));

        var plan = await Service(h, placer).PreviewHoldMovesAsync(s.WallId, HoldMovePromoteTests.Confirm(s, CarryKind.Changed));

        var move = Assert.Single(plan.Moves, m => m.Outcome != HoldMoveOutcome.Stayed);
        Assert.Equal((s.OldMover, HoldMoveSource.ThreeD, HoldMoveOutcome.Removed), (move.OldHoldId, move.Measure.Source, move.Outcome));
        Assert.Equal(340, move.Measure.DistanceMm, 1);

        // Nothing was written: the staged rows still have no placement.
        await using var db = h.CreateContext();
        Assert.All(await db.Holds.Where(x => x.Generation == 3).ToListAsync(), x => Assert.Null(x.FacetId));
    }

    [Fact]
    public async Task ThePlanVersionCoversTheProvisionalMeasurements_AndPromoteRefusesAChangedOne()
    {
        using var h = new WallTestHarness();
        var s = await HoldMovePromoteTests.SeedAsync(h, movedToA: 1000, placeStaged: false);
        var at340 = new FakePlacer(HoldMovePromoteTests.Provisional(s, 1340));
        var at60 = new FakePlacer(HoldMovePromoteTests.Provisional(s, 1060));
        var confirmation = HoldMovePromoteTests.Confirm(s, CarryKind.Changed);

        var confirmed = await Service(h, at340).PreviewHoldMovesAsync(s.WallId, confirmation);
        var changed = await Service(h, at60).PreviewHoldMovesAsync(s.WallId, confirmation);

        Assert.NotEqual(confirmed.Version, changed.Version);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(h, at60).PromoteAsync(s.WallId, confirmation with { ExpectedMovesVersion = confirmed.Version }, s.SessionId));
        await Service(h, at340).PromoteAsync(s.WallId, confirmation with { ExpectedMovesVersion = confirmed.Version }, s.SessionId);

        await using var db = h.CreateContext();
        Assert.Equal(HoldMoveOutcome.Removed, (await db.BoulderHoldMoves.SingleAsync()).Outcome);
    }

    [Fact]
    public async Task WhenRegistrationFails_ThePlanFallsBackToNothingWithoutAScale()
    {
        using var h = new WallTestHarness();
        var s = await HoldMovePromoteTests.SeedAsync(h, movedToA: 1000, placeStaged: false);

        var plan = await Service(h, new FakePlacer([])).PreviewHoldMovesAsync(s.WallId, HoldMovePromoteTests.Confirm(s));

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public async Task Remeasure_AfterPlacement_MarksTheBoulderForReview_WithoutChangingItsHolds()
    {
        using var h = new WallTestHarness();
        var s = await HoldMovePromoteTests.SeedAsync(h, movedToA: 1000, placeStaged: false);
        await s.Service.PromoteAsync(s.WallId, HoldMovePromoteTests.Confirm(s), s.SessionId);
        await using (var db = h.CreateContext())
        {
            Assert.False((await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);

            // The placement run puts the hold where the registration says it really is: 34 cm from where it was.
            await HoldMovePromoteTests.PlaceNewAsync(db, s, 1340);
        }

        await using (var db = h.CreateContext())
        {
            var marked = await HoldMoveRemeasurer.RunAsync(db, s.WallId, HoldMovePromoteTests.Loose, NullLogger.Instance);
            Assert.Equal(1, marked);
        }

        await using var check = h.CreateContext();
        var boulder = await check.Boulders.SingleAsync(b => b.Id == s.BoulderId);
        Assert.True(boulder.NeedsReview);

        // Nothing was taken off the boulder: the decision is a person's.
        Assert.Equal(2, await check.BoulderHolds.CountAsync(b => b.BoulderId == s.BoulderId));
        var row = await check.BoulderHoldMoves.SingleAsync();
        Assert.Equal((HoldMoveOutcome.Kept, HoldMoveOutcome.Possible), (row.Outcome, row.RemeasuredOutcome));
        Assert.Equal(340, row.RemeasuredDistanceMm!.Value, 1);
        var link = await check.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldMover);
        Assert.Equal((HoldMoveSource.ThreeD, HoldMoveOutcome.Possible), (link.MoveSource, link.MoveOutcome));

        var shown = Assert.Single(await h.BoulderService.GetBoulderMovesAsync(s.BoulderId));
        Assert.Contains("Measured again in 3D", shown.Text);
        Assert.Contains("34 cm", shown.Text);
    }

    [Fact]
    public async Task Remeasure_WhenTheVerdictStays_ChangesNothing()
    {
        using var h = new WallTestHarness();
        var s = await HoldMovePromoteTests.SeedAsync(h, movedToA: 1000, placeStaged: false);
        await s.Service.PromoteAsync(s.WallId, HoldMovePromoteTests.Confirm(s), s.SessionId);
        await using (var db = h.CreateContext())
        {
            await HoldMovePromoteTests.PlaceNewAsync(db, s, 1005);
        }

        await using (var db = h.CreateContext())
        {
            Assert.Equal(0, await HoldMoveRemeasurer.RunAsync(db, s.WallId, HoldMovePromoteTests.Loose, NullLogger.Instance));
        }

        await using var check = h.CreateContext();
        Assert.False((await check.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
        Assert.Empty(await check.BoulderHoldMoves.ToListAsync());
    }

    [Fact]
    public async Task Remeasure_ThatConfirmsTheHoldStayed_ClearsTheReviewMarkAnEarlierEstimateSet()
    {
        using var h = new WallTestHarness();
        var s = await HoldMovePromoteTests.SeedAsync(h, movedToA: 1000, placeStaged: false);

        // At confirm time the provisional registration put the hold 6 cm away: kept, boulder marked.
        var provisional = new FakePlacer(HoldMovePromoteTests.Provisional(s, 1060));
        await Service(h, provisional).PromoteAsync(s.WallId, HoldMovePromoteTests.Confirm(s), s.SessionId);
        await using (var db = h.CreateContext())
        {
            Assert.True((await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);

            // The real placement run says it did not move at all.
            await HoldMovePromoteTests.PlaceNewAsync(db, s, 1002);
        }

        await using (var db = h.CreateContext())
        {
            await HoldMoveRemeasurer.RunAsync(db, s.WallId, HoldMovePromoteTests.Loose, NullLogger.Instance);
        }

        await using var check = h.CreateContext();
        Assert.False((await check.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
        Assert.Equal(HoldMoveOutcome.Stayed, (await check.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldMover)).MoveOutcome);
    }

    [Fact]
    public async Task Remeasure_WithTooFewNeighbours_ChangesNothing()
    {
        using var h = new WallTestHarness();
        var s = await HoldMovePromoteTests.SeedAsync(h, movedToA: 1000, placeStaged: false);
        await s.Service.PromoteAsync(s.WallId, HoldMovePromoteTests.Confirm(s), s.SessionId);
        await using (var db = h.CreateContext())
        {
            await HoldMovePromoteTests.PlaceNewAsync(db, s, 1340);
        }

        await using (var db = h.CreateContext())
        {
            Assert.Equal(0, await HoldMoveRemeasurer.RunAsync(db, s.WallId, new HoldMoveOptions(), NullLogger.Instance));
        }

        await using var check = h.CreateContext();
        Assert.False((await check.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
    }
}
