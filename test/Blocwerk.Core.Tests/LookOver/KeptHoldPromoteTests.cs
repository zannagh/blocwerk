// <copyright file="KeptHoldPromoteTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// What an update does with a hold it could not find: the copy it keeps is marked "kept, not confirmed" and is a
/// look-over finding, whether a boulder uses it or not, and the finding says when the copy lies under a new hold. A
/// hold the update found again is never marked.
/// </summary>
public class KeptHoldPromoteTests
{
    [Fact]
    public async Task AHoldTheUpdateDidNotFind_IsKeptUnconfirmed_AndAFinding_ThoughThePhotoLooksUnchanged()
    {
        using var h = new WallTestHarness();

        var w = await ApplyAsync(h, targetDetected: false, score: 0.9);

        var boulder = await BoulderAsync(h, w.BoulderId);
        var copy = (await HoldAsync(h, boulder.BoulderHolds.Single().HoldId))!;
        Assert.Equal((3, 3), (copy.KeptUnconfirmedGeneration, (await WallAsync(h, w.WallId)).KeptHoldsGeneration));
        var finding = await FindingAsync(h, w.WallId);
        Assert.Equal((LookOverFindingKind.NotFound, (Guid?)copy.Id, true, false), (finding.Kind, finding.HoldId, finding.HoldKept, finding.Covered));
        Assert.NotNull((await WallAsync(h, w.WallId)).LookOverPendingSince);

        // Every other hold was found again.
        await using var db = h.CreateContext();
        Assert.Equal(1, await db.Holds.CountAsync(x => x.KeptUnconfirmedGeneration != null));
    }

    [Fact]
    public async Task AReplacedHold_IsKeptUnconfirmed_AndTheHoldThatTookItsPlaceIsNot()
    {
        using var h = new WallTestHarness();

        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);

        var finding = await FindingAsync(h, w.WallId);
        Assert.Equal((LookOverFindingKind.ReplacedByOther, true, true, w.DetectionId), (finding.Kind, finding.HoldKept, finding.Covered, finding.OtherHoldId));
        Assert.Equal(3, (await HoldAsync(h, finding.HoldId))!.KeptUnconfirmedGeneration);
        Assert.Null((await HoldAsync(h, w.DetectionId))!.KeptUnconfirmedGeneration);
    }

    [Fact]
    public async Task AHoldCarriedOntoItsDetection_IsNotMarked()
    {
        using var h = new WallTestHarness();

        var w = await ApplyAsync(h, targetDetected: true, score: 0.9);

        await using var db = h.CreateContext();
        Assert.False(await db.Holds.AnyAsync(x => x.WallId == w.WallId && x.KeptUnconfirmedGeneration != null));
        Assert.Equal(3, (await WallAsync(h, w.WallId)).KeptHoldsGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHoldNoBoulderUses_IsAFindingToo_AndSaysWhenItLiesUnderANewHold(bool covered)
    {
        using var h = new WallTestHarness();

        var k = await KeptHoldFixture.ApplyAsync(h, covered);

        var findings = await FindingsAsync(h, k.Wall.WallId);
        Assert.Equal(2, findings.Count);
        var unused = findings.Single(f => f.HoldId == k.UnusedId);
        Assert.Equal((0, true, covered, k.BigId), (unused.BouldersAtApply, unused.HoldKept, unused.Covered, unused.OtherHoldId));
        Assert.Equal(covered ? LookOverFindingKind.SameSpot : LookOverFindingKind.NotFound, unused.Kind);
        Assert.Equal(3, (await HoldAsync(h, k.UnusedId))!.KeptUnconfirmedGeneration);
        Assert.Null((await HoldAsync(h, k.FoundId))!.KeptUnconfirmedGeneration);

        var state = (await Service(h).GetStateAsync(k.Wall.WallId))!;
        Assert.Equal((2, 2), (state.OpenFindings, state.MissingHolds));
    }

    [Fact]
    public void UnderANewHold_MeansTheOldCentreLiesInsideItsOutline_OrItsCircleWhenItHasNone()
    {
        Hold At(double x, double y, double radius = 0.03) => new() { X = x, Y = y, Radius = radius };
        var old = At(0.5, 0.5);
        var traced = At(0.6, 0.5, 0.2);
        traced.ShapePoints = KeptHoldFixture.Box(0.05, 0.05);

        // Within the traced hold's radius, but outside its outline: beside it, not under it.
        Assert.False(SameSpotFinder.Covers(traced, old, (2000, 1000)));
        traced.X = 0.52;
        Assert.True(SameSpotFinder.Covers(traced, old, (2000, 1000)));

        // Without an outline the circle decides, in pixels of the photo (0.05 of 2000 px is 100 px).
        Assert.True(SameSpotFinder.Covers(At(0.54, 0.5, 0.05), old, (2000, 1000)));
        Assert.False(SameSpotFinder.Covers(At(0.56, 0.5, 0.05), old, (2000, 1000)));
    }
}
