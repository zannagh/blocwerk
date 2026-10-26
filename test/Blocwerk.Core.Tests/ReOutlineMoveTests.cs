// <copyright file="ReOutlineMoveTests.cs" company="Blocwerk">
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
/// A hand-placed circle replaced by the detected outline shifts the hold's centre while nothing moved: that is a re-outline,
/// recorded as stayed, and neither the promote nor the 3D re-measurement after placement marks its boulders. A hold whose
/// detection really lies elsewhere still moved. On SQLite and, with <see cref="PostgresTestDatabase.EnvVar"/> set, PostgreSQL.
/// </summary>
public class ReOutlineMoveTests
{
    private static readonly HoldMoveOptions Options = new();

    [Fact]
    public void ACircleReplacedByALargerOutlineAroundIt_IsAReOutline()
    {
        var old = new Hold { X = 0.5, Y = 0.5, Radius = 0.02 };
        var twin = new Hold { X = 0.51, Y = 0.5, Radius = 0.03, ShapePoints = Square(0.03) };

        Assert.True(ReOutlineRule.Explains(old, twin, At(0.5, 0.5), 30, null, Options));
    }

    [Fact]
    public void AnUnchangedHoldThatShifted_IsNoReOutline_ItIsMeasuredAsUsual()
    {
        // Identical circles a little apart, and a 10 cm jug moved 3.5 cm: the outline did not change, so nothing explains the shift.
        var old = new Hold { X = 0.5, Y = 0.5, Radius = 0.0125, ShapePoints = Square(0.0125) };
        var same = new Hold { X = 0.5, Y = 0.5, Radius = 0.0125, ShapePoints = Square(0.0125) };
        var jug = new Hold { X = 0.50875, Y = 0.5, Radius = 0.0125, ShapePoints = Square(0.0125) };

        Assert.False(ReOutlineRule.Explains(old, same, At(old), 0, null, Options));
        Assert.False(ReOutlineRule.Explains(old, jug, At(old), 35, null, Options));
    }

    [Fact]
    public void AVolumeMovedTenCm_IsNoReOutline()
    {
        var old = new Hold { X = 0.5, Y = 0.5, Radius = 0.0375, ShapePoints = Square(0.0375) };
        var moved = new Hold { X = 0.5125, Y = 0.5, Radius = 0.0375, ShapePoints = Square(0.0375) };

        Assert.False(ReOutlineRule.Explains(old, moved, At(old), 100, null, Options));
    }

    [Fact]
    public void ARailTurnedInPlace_IsNoReOutline()
    {
        // A 2:1 rail turned by 90°: the same area, and it turned past the rotation threshold.
        var old = new Hold { X = 0.5, Y = 0.5, Radius = 0.03, ShapePoints = Rect(0.03, 0.015) };
        var turned = new Hold { X = 0.5, Y = 0.5, Radius = 0.03, ShapePoints = Rect(0.015, 0.03) };

        Assert.False(ReOutlineRule.Explains(old, turned, At(old), 5, 90, Options));
        Assert.False(ReOutlineRule.Explains(old, turned, At(old), 5, null, Options));
    }

    [Fact]
    public void ACircleReplacedByAnOutline_ThatTurned_IsNoReOutline()
    {
        var old = new Hold { X = 0.5, Y = 0.5, Radius = 0.02 };
        var twin = new Hold { X = 0.51, Y = 0.5, Radius = 0.03, ShapePoints = Square(0.03) };

        Assert.False(ReOutlineRule.Explains(old, twin, At(0.5, 0.5), 30, Options.MinRotationDeg, Options));
    }

    [Fact]
    public void AChangedOutline_WithMoreShiftThanTheChangeExplains_IsNoReOutline()
    {
        // The circle (radius 80 px) became a 240 x 180 px outline, but 70 px of the 110 px shift are not the size change.
        var old = new Hold { X = 0.5, Y = 0.5, Radius = 0.02 };
        var twin = new Hold { X = 0.5275, Y = 0.5, Radius = 0.03, ShapePoints = Rect(0.03, 0.03) };

        Assert.False(ReOutlineRule.Explains(old, twin, At(0.5, 0.5), 90, null, Options));
    }

    [Fact]
    public void WithoutTheWarp_NothingIsExplained()
    {
        var old = new Hold { X = 0.5, Y = 0.5, Radius = 0.02 };
        var twin = new Hold { X = 0.51, Y = 0.5, Radius = 0.03, ShapePoints = Square(0.03) };

        Assert.False(ReOutlineRule.Explains(old, twin, null, 30, null, Options));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReOutlinedHold_StaysOnItsBoulder_UnmarkedByThePromoteAndTheReMeasurement(bool postgres)
    {
        using var h = Harness(postgres);
        var s = await HoldMovePromoteTests.SeedAsync(h, movedToA: 1060);
        await using (var db = h.CreateContext())
        {
            var mover = await db.Holds.SingleAsync(x => x.Id == s.NewMover);
            (mover.X, mover.ShapePoints) = (0.51, Square(0.03));
            await db.SaveChangesAsync();
        }

        await s.Service.PromoteAsync(s.WallId, WithWarp(s), s.SessionId);
        await using (var db = h.CreateContext())
        {
            var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldMover);
            Assert.Equal((HoldMoveOutcome.Stayed, HoldMoveSource.ReOutline), (link.MoveOutcome, link.MoveSource));
            Assert.False((await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
            await HoldMovePromoteTests.PlaceNewAsync(db, s, 1010);
            Assert.Equal(0, await HoldMoveRemeasurer.RunAsync(db, s.WallId, HoldMovePromoteTests.Loose, NullLogger.Instance));
        }

        await using var after = h.CreateContext();
        Assert.False((await after.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHoldThatReallyMoved_IsStillMeasured_AsASuspicion_ThatMarksNothingAndKeepsTheWarpedSpot(bool postgres)
    {
        using var h = Harness(postgres);
        var s = await HoldMovePromoteTests.SeedAsync(h, movedToA: 1060);
        await using (var db = h.CreateContext())
        {
            (await db.Holds.SingleAsync(x => x.Id == s.NewMover)).X = 0.56;
            await db.SaveChangesAsync();
        }

        await s.Service.PromoteAsync(s.WallId, WithWarp(s), s.SessionId);

        await using var check = h.CreateContext();
        Assert.Equal(HoldMoveOutcome.Suspected, (await check.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldMover)).MoveOutcome);
        Assert.False((await check.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
        Assert.Empty(await check.BoulderHoldMoves.ToListAsync());

        // Nobody said it moved: the hold stays where the alignment puts the old one, not on the detection (x 0.56).
        Assert.Equal(0.5, (await check.Holds.SingleAsync(x => x.Id == s.NewMover)).X, 6);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnmovedIdenticalHold_StaysByTheOrdinaryMeasure_NotAsAReOutline(bool postgres)
    {
        using var h = Harness(postgres);
        var s = await HoldMovePromoteTests.SeedAsync(h, movedToA: 1010);

        await s.Service.PromoteAsync(s.WallId, WithWarp(s), s.SessionId);

        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldMover);
        Assert.Equal((HoldMoveOutcome.Stayed, HoldMoveSource.ThreeD), (link.MoveOutcome, link.MoveSource));
    }

    [SkippableTheory]
    [InlineData(false, 1340)]
    [InlineData(true, 1340)]
    [InlineData(false, 1060)]
    [InlineData(true, 1060)]
    public async Task AReOutlinedHold_ThatThe3DMeasureFindsMoved_IsAskedAboutAfterAll_NotMarked(bool postgres, double placedAtA)
    {
        using var h = Harness(postgres);
        var s = await HoldMovePromoteTests.SeedAsync(h, movedToA: 1060);
        await using (var db = h.CreateContext())
        {
            var mover = await db.Holds.SingleAsync(x => x.Id == s.NewMover);
            (mover.X, mover.ShapePoints) = (0.51, Square(0.03));
            await db.SaveChangesAsync();
        }

        await s.Service.PromoteAsync(s.WallId, WithWarp(s), s.SessionId);
        await using var check = h.CreateContext();
        await HoldMovePromoteTests.PlaceNewAsync(check, s, placedAtA);

        // 60 mm and 340 mm in 3D are past the re-outline's 25 mm bar; nobody confirmed a move, so it is asked, not marked.
        Assert.Equal(0, await HoldMoveRemeasurer.RunAsync(check, s.WallId, HoldMovePromoteTests.Loose, NullLogger.Instance));
        var link = await check.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldMover);
        Assert.Equal(HoldMoveOutcome.Suspected, link.MoveOutcome);
        Assert.False((await check.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
        var finding = await check.WallLookOverFindings.SingleAsync();
        Assert.Equal((LookOverFindingKind.SuspectedMove, s.OldMover, s.NewMover), (finding.Kind, finding.OldHoldId, finding.HoldId));
    }

    private static WarpedFootprint At(double x, double y) => new(new HoldPositionNorm(x, y), null, (4000, 3000));

    /// <summary>The old hold where it was, its outline warped along (an unmoved photo).</summary>
    private static WarpedFootprint At(Hold old) =>
        new(new HoldPositionNorm(old.X, old.Y), old.ShapePoints?.Select(p => new HoldPositionNorm(old.X + p.Dx, old.Y + p.Dy)).ToList(), (4000, 3000));

    private static List<ShapePoint> Rect(double halfX, double halfY) =>
    [
        new() { Dx = -halfX, Dy = -halfY }, new() { Dx = halfX, Dy = -halfY }, new() { Dx = halfX, Dy = halfY }, new() { Dx = -halfX, Dy = halfY },
    ];

    private static WallTestHarness Harness(bool postgres)
    {
        Skip.If(postgres && !PostgresTestDatabase.Enabled, PostgresTestDatabase.SkipReason);
        return postgres ? new WallTestHarness(PostgresTestDatabase.Create()) : new WallTestHarness();
    }

    /// <summary>The confirmation with the matcher's warp: every old hold predicted where it was.</summary>
    private static BigUpdateConfirmation WithWarp(HoldMovePromoteTests.Scenario s) => HoldMovePromoteTests.Confirm(s) with
    {
        CarriedWarpPositions = new Dictionary<Guid, HoldPositionNorm>
        {
            [s.OldMover] = new(0.5, 0.5),
            [s.OldStayer] = new(0.2, 0.5),
        },
    };

    private static List<ShapePoint> Square(double half) =>
    [
        new() { Dx = -half, Dy = -half }, new() { Dx = half, Dy = -half }, new() { Dx = half, Dy = half }, new() { Dx = -half, Dy = half },
    ];
}
