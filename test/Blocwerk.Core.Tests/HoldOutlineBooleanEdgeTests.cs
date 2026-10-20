// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Detection.Outlines;

namespace Blocwerk.Core.Tests;

/// <summary>Outlines the merge and split must not get wrong: self-overlapping rings, and pockets that cut a part in two.</summary>
public sealed class HoldOutlineBooleanEdgeTests
{
    [Fact]
    public void AnOutlineTracedTwiceAroundCountsOnce_NotAsAnEvenOddHole()
    {
        var once = Square(0.30, 0.50, 0.02);
        var twice = new OutlineRings([.. once.Outer, .. once.Outer], []);
        var other = Square(0.33, 0.50, 0.02);

        var merged = HoldOutlineBoolean.Merge(twice, other, 1.0, maxGap: 0.05)!;

        Assert.True(merged.Shape.Covers(new P2(0.30, 0.50)));
        Assert.Equal(HoldOutlineBoolean.Merge(once, other, 1.0, 0.05)!.Shape.Area, merged.Shape.Area, 6);
    }

    [Fact]
    public void ABowTieOutlineKeepsBothLobesInTheUnion()
    {
        // The two triangles of a bow tie wind opposite ways; nonzero after simplifying keeps both filled.
        var bowTie = new OutlineRings([new P2(0.40, 0.40), new P2(0.50, 0.50), new P2(0.50, 0.40), new P2(0.40, 0.50)], []);
        var other = Square(0.45, 0.45, 0.01);

        var merged = HoldOutlineBoolean.Merge(bowTie, other, 1.0, maxGap: 0.05)!;

        Assert.True(merged.Shape.Covers(new P2(0.41, 0.45)) && merged.Shape.Covers(new P2(0.49, 0.45)));
    }

    [Fact]
    public void ACutWhoseSideAPocketCutsInTwoIsRefused_NotSilentlyShrunkToTheLargerPiece()
    {
        // Sloppy data: a pocket that reaches out over the top edge. Cutting across it leaves the top side in two.
        var pocket = new List<P2> { new(0.49, 0.40), new(0.51, 0.40), new(0.51, 0.47), new(0.49, 0.47) };
        var shape = new OutlineRings([new P2(0.40, 0.42), new P2(0.60, 0.42), new P2(0.60, 0.60), new P2(0.40, 0.60)], [pocket]);

        var cut = HoldOutlineBoolean.Split(shape, [new P2(0.35, 0.45), new P2(0.65, 0.45)], 1.0);

        Assert.Contains("pocket", cut.Refusal);
    }

    [Fact]
    public void ACutThatLoopsOverItselfInsideTheHoldStillTilesIt()
    {
        var shape = Square(0.50, 0.50, 0.05);
        List<P2> stroke = [new(0.50, 0.40), new(0.50, 0.49), new(0.53, 0.51), new(0.47, 0.51), new(0.50, 0.49), new(0.50, 0.60)];

        var cut = HoldOutlineBoolean.Split(shape, stroke, 1.0);

        Assert.Null(cut.Refusal);
        Assert.Equal(shape.Area, cut.Larger!.Area + cut.Smaller!.Area, 4);
    }

    private static OutlineRings Square(double x, double y, double h) =>
        new([new P2(x - h, y - h), new P2(x + h, y - h), new P2(x + h, y + h), new P2(x - h, y + h)], []);
}
