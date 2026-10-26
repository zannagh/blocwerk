// <copyright file="HoldMoveAssumeNothingMovedTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="HoldMoveOptions.AssumeNothingMoved"/>: the person asserts holds were only added. Every carried hold keeps its
/// pure warp geometry, no measured move or "not found again" marks a boulder, and explicit answers still apply. Off, the
/// promote is unchanged.
/// </summary>
public class HoldMoveAssumeNothingMovedTests
{
    private static readonly HoldMoveOptions NothingMoved = new() { MinNeighbours = 3, AssumeNothingMoved = true };

    [Fact]
    public async Task Off_ARefoundHoldTakesTheDetectionsGeometry()
    {
        using var h = new WallTestHarness();
        var s = await HoldMovePromoteTests.SeedAsync(h, movedToA: 1060);
        var (warp, shapes) = await PrepareGeometryAsync(h, s);

        await s.Service.PromoteAsync(s.WallId, HoldMovePromoteTests.Confirm(s) with { CarriedWarpPositions = warp, CarriedWarpShapes = shapes }, s.SessionId);

        await using var db = h.CreateContext();
        var mover = await db.Holds.SingleAsync(x => x.Id == s.NewMover);
        Assert.Equal((0.5, 0.5, 0.02), (mover.X, mover.Y, mover.Radius));
        Assert.Equal(HoldOutlineSource.AutoContour, mover.OutlineSource);
    }

    [Fact]
    public async Task On_ATwinGetsTheWarpCentre_ACircleStaysACircleWithAScaledRadius_AndAnOutlineIsWarped()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, movedToA: 1060);
        var (warp, shapes) = await PrepareGeometryAsync(h, s);

        await s.Service.PromoteAsync(s.WallId, HoldMovePromoteTests.Confirm(s) with { CarriedWarpPositions = warp, CarriedWarpShapes = shapes }, s.SessionId);

        await using var db = h.CreateContext();
        var mover = await db.Holds.SingleAsync(x => x.Id == s.NewMover);
        Assert.Equal(warp[s.OldMover].X, mover.X, 9);
        Assert.Equal(warp[s.OldMover].Y, mover.Y, 9);
        Assert.Equal(0.022, mover.Radius, 6);
        Assert.Null(mover.ShapePoints);
        Assert.Null(mover.OutlineSource);

        var stayer = await db.Holds.SingleAsync(x => x.Id == s.NewStayer);
        Assert.Equal(warp[s.OldStayer].X, stayer.X, 9);
        Assert.Equal(HoldOutlineSource.Manual, stayer.OutlineSource);
        Assert.Equal(
            shapes[s.OldStayer].Select(v => (Math.Round(v.X - stayer.X, 9), Math.Round(v.Y - stayer.Y, 9))),
            stayer.ShapePoints!.Select(p => (Math.Round(p.Dx, 9), Math.Round(p.Dy, 9))));
        Assert.Equal(HoldGenerationLinkKind.Same, (await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldMover)).Kind);
    }

    [Fact]
    public async Task On_ACloneGetsAScaledRadius()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, movedToA: 1000);
        var (warp, shapes) = await PrepareGeometryAsync(h, s);
        var confirmation = HoldMovePromoteTests.Confirm(s) with { CarriedWarpPositions = warp, CarriedWarpShapes = shapes };
        var lone = s.Extra[0].Old;
        confirmation = confirmation with
        {
            Carryover = confirmation.Carryover.Select(d => d.OldHoldId == lone ? new CarryoverDecision(lone, CarryKind.Carried, null) : d).ToList(),
        };

        await s.Service.PromoteAsync(s.WallId, confirmation, s.SessionId);

        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == lone);
        var clone = await db.Holds.SingleAsync(x => x.Id == link.NewHoldId);
        Assert.Equal(warp[lone].X, clone.X, 9);
        Assert.Equal(0.022, clone.Radius, 6);
    }

    [Fact]
    public async Task On_ASixCmMove_MarksNoBoulder()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, movedToA: 1060);

        var plan = await s.Service.PreviewHoldMovesAsync(s.WallId, HoldMovePromoteTests.Confirm(s));
        await s.Service.PromoteAsync(s.WallId, HoldMovePromoteTests.Confirm(s) with { ExpectedMovesVersion = plan.Version }, s.SessionId);

        Assert.All(plan.Moves, m => Assert.Equal(HoldMoveOutcome.Stayed, m.Outcome));
        await using var db = h.CreateContext();
        Assert.False((await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
        Assert.Empty(await db.BoulderHoldMoves.ToListAsync());
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldMover);
        Assert.Equal((HoldMoveOutcome.Stayed, 60.0), (link.MoveOutcome, link.MoveDistanceMm!.Value));
    }

    [Fact]
    public async Task On_AMoveAPersonConfirmed_IsStillMeasured()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, movedToA: 1340);

        await s.Service.PromoteAsync(s.WallId, HoldMovePromoteTests.Confirm(s, CarryKind.Changed), s.SessionId);

        await using var db = h.CreateContext();
        Assert.Equal(HoldMoveOutcome.Removed, (await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldMover)).MoveOutcome);
        Assert.True((await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
    }

    [Theory]
    [InlineData(HoldMoveOutcome.NotFound)]
    [InlineData(HoldMoveOutcome.Replaced)]
    public async Task On_AHoldNotFoundAgainOrReplaced_MarksNoBoulder(HoldMoveOutcome outcome)
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, movedToA: 1000);
        var confirmation = WithoutMoverTwin(s) with { NotFoundAgain = new Dictionary<Guid, HoldMoveOutcome> { [s.OldMover] = outcome } };

        var plan = await s.Service.PreviewHoldMovesAsync(s.WallId, confirmation);
        await s.Service.PromoteAsync(s.WallId, confirmation, s.SessionId);

        Assert.True(plan.NotFound is null or { Count: 0 });
        await using var db = h.CreateContext();
        Assert.False((await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
        Assert.Empty(await db.BoulderHoldMoves.ToListAsync());
    }

    [Fact]
    public async Task On_AHoldAnsweredGone_StillFreezesItsBoulders()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, movedToA: 1000);
        var confirmation = HoldMovePromoteTests.Confirm(s);
        confirmation = confirmation with
        {
            Carryover = confirmation.Carryover.Select(d => d.OldHoldId == s.OldMover ? new CarryoverDecision(s.OldMover, CarryKind.Removed, null) : d).ToList(),
        };

        await s.Service.PromoteAsync(s.WallId, confirmation, s.SessionId);

        await using var db = h.CreateContext();
        Assert.True((await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).IsHistoric);
    }

    [Fact]
    public async Task On_AHandPlacedMerge_KeepsItsPureWarp()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, movedToA: 1000);
        var (warp, shapes) = await PrepareGeometryAsync(h, s);

        await s.Service.PromoteAsync(
            s.WallId,
            HoldMovePromoteTests.Confirm(s) with { CarriedWarpPositions = warp, CarriedWarpShapes = shapes, HandPlacedMergeOldIds = [s.OldMover, s.OldStayer] },
            s.SessionId);

        await using var db = h.CreateContext();
        var mover = await db.Holds.SingleAsync(x => x.Id == s.NewMover);
        Assert.Equal((warp[s.OldMover].X, 0.022, (List<ShapePoint>?)null), (Math.Round(mover.X, 9), Math.Round(mover.Radius, 6), mover.ShapePoints));
        var stayer = await db.Holds.SingleAsync(x => x.Id == s.NewStayer);
        Assert.Equal(shapes[s.OldStayer].Count, stayer.ShapePoints!.Count);
        Assert.Equal(HoldOutlineSource.Manual, stayer.OutlineSource);
    }

    [Fact]
    public async Task On_TheRemeasureAfterPlacement_MarksNothing()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, movedToA: 1000, placeStaged: false);
        await s.Service.PromoteAsync(s.WallId, HoldMovePromoteTests.Confirm(s), s.SessionId);
        await using (var db = h.CreateContext())
        {
            await HoldMovePromoteTests.PlaceNewAsync(db, s, 1340);
        }

        await using (var db = h.CreateContext())
        {
            Assert.Equal(0, await HoldMoveRemeasurer.RunAsync(db, s.WallId, NothingMoved, NullLogger.Instance));
        }

        await using var check = h.CreateContext();
        Assert.False((await check.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
        Assert.Empty(await check.BoulderHoldMoves.ToListAsync());
    }

    [Fact]
    public void Policy_WithTheOptionOn_OnlyAConfirmedMoveIsMeasured()
    {
        var measure = new HoldMoveMeasure(60, HoldMoveSource.TwoD, null, Corroborated: true);

        Assert.Equal(HoldMoveOutcome.Stayed, HoldMovePolicy.Classify(measure, NothingMoved));
        Assert.Equal(HoldMoveOutcome.Kept, HoldMovePolicy.Classify(measure, NothingMoved, confirmedMove: true));
        Assert.Equal(HoldMoveOutcome.Kept, HoldMovePolicy.Classify(measure, HoldMovePromoteTests.Loose));
    }

    /// <summary>The shared scenario, promoted by a service with the option on.</summary>
    private static async Task<HoldMovePromoteTests.Scenario> SeedAsync(WallTestHarness h, double movedToA, bool placeStaged = true)
    {
        var s = await HoldMovePromoteTests.SeedAsync(h, movedToA, placeStaged);
        var service = new WallBigUpdateService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, new PositionHoldMatcher(), NullLogger<WallBigUpdateService>.Instance, moveOptions: NothingMoved);
        return s with { Service = service };
    }

    /// <summary>
    /// A warp that scales the photo by 1.1 about its centre and shifts it right by 0.01; the old stayer gets a hand-traced
    /// square outline, and every detection a contour of its own (what the promote must NOT keep when the option is on).
    /// </summary>
    private static async Task<(Dictionary<Guid, HoldPositionNorm> Warp, Dictionary<Guid, IReadOnlyList<HoldPositionNorm>> Shapes)> PrepareGeometryAsync(
        WallTestHarness h, HoldMovePromoteTests.Scenario s)
    {
        static HoldPositionNorm Warp(double x, double y) => new(0.5 + (1.1 * (x - 0.5)) + 0.01, 0.5 + (1.1 * (y - 0.5)));
        List<ShapePoint> Square(double d) => [new() { Dx = -d, Dy = -d }, new() { Dx = d, Dy = -d }, new() { Dx = d, Dy = d }, new() { Dx = -d, Dy = d }];

        await using var db = h.CreateContext();
        var oldIds = new[] { s.OldMover, s.OldStayer }.Concat(s.Extra.Select(e => e.Old)).ToList();
        var olds = await db.Holds.Where(x => oldIds.Contains(x.Id)).ToListAsync();
        var stayer = olds.Single(x => x.Id == s.OldStayer);
        (stayer.ShapePoints, stayer.OutlineSource) = (Square(0.03), HoldOutlineSource.Manual);
        var newIds = new[] { s.NewMover, s.NewStayer }.Concat(s.Extra.Select(e => e.New)).ToList();
        foreach (var detection in await db.Holds.Where(x => newIds.Contains(x.Id)).ToListAsync())
        {
            (detection.ShapePoints, detection.OutlineSource) = (Square(0.01), HoldOutlineSource.AutoContour);
        }

        await db.SaveChangesAsync();
        var warp = olds.ToDictionary(x => x.Id, x => Warp(x.X, x.Y));
        var shapes = new Dictionary<Guid, IReadOnlyList<HoldPositionNorm>>
        {
            [s.OldStayer] = stayer.ShapePoints!.Select(p => Warp(stayer.X + p.Dx, stayer.Y + p.Dy)).ToList(),
        };
        return (warp, shapes);
    }

    /// <summary>The mover carried without a twin (not found again), everything else as the shared confirmation.</summary>
    private static BigUpdateConfirmation WithoutMoverTwin(HoldMovePromoteTests.Scenario s)
    {
        var confirmation = HoldMovePromoteTests.Confirm(s);
        return confirmation with
        {
            Carryover = confirmation.Carryover.Select(d => d.OldHoldId == s.OldMover ? new CarryoverDecision(s.OldMover, CarryKind.Carried, null) : d).ToList(),
        };
    }
}
