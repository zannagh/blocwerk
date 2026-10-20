// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Tests.RealData;

namespace Blocwerk.Core.Tests;

/// <summary>The merge and split geometry: union, the gap bridge, holes and the polyline cut, on synthetic and real outlines.</summary>
public sealed class HoldOutlineBooleanTests
{
    private const double Aspect = 4.0 / 3.0;

    [Fact]
    public void OverlappingOutlinesUniteIntoOnePieceWithoutABridge()
    {
        var a = Blob(0.40, 0.50, 0.03);
        var b = Blob(0.44, 0.50, 0.03, phase: 1.3);

        var merged = HoldOutlineBoolean.Merge(a, b, Aspect, maxGap: 0.05)!;

        Assert.False(merged.Bridged);
        Assert.Equal(0, merged.Gap);
        Assert.InRange(merged.Shape.Area, Math.Max(a.Area, b.Area), a.Area + b.Area);
        AssertCovers(merged.Shape, a);
        AssertCovers(merged.Shape, b);
    }

    [Fact]
    public void AGapIsBridgedWithANeckAndTheFarSidesStayAsTraced()
    {
        var a = Blob(0.40, 0.50, 0.03);
        var b = Blob(0.40 + (0.066 / Aspect), 0.50, 0.03, phase: 0.7);

        var merged = HoldOutlineBoolean.Merge(a, b, Aspect, maxGap: 0.05)!;

        Assert.True(merged.Bridged);
        Assert.InRange(merged.Gap, 0.002, 0.012);
        AssertCovers(merged.Shape, a);
        AssertCovers(merged.Shape, b);

        // The bridge adds a neck, not a hull: well under the convex hull's extra area.
        double hullArea = ShapeGeometry.Area(ShapeGeometry.ConvexHull(a.Outer.Concat(b.Outer)));
        Assert.InRange(merged.Shape.Area, a.Area + b.Area + 1e-5, hullArea - 1e-5);

        // The neck is really there: the gap's midpoint is on the hold.
        Assert.True(merged.Shape.Covers(new P2(0.40 + (0.033 / Aspect), 0.50)));

        // Away from the gap the outline is the traced one: A's leftmost vertex is still on the boundary.
        var far = a.Outer.MinBy(p => p.X);
        Assert.True(ShapeGeometry.Distance(far, merged.Shape.Outer) < 1e-3 || (ShapeGeometry.ClosestOnBoundary(far, merged.Shape.Outer) - far).Length < 1e-3);
    }

    [Fact]
    public void HoldsFurtherApartThanTheLimitAreNotMerged()
    {
        var a = Blob(0.20, 0.50, 0.02);
        var b = Blob(0.60, 0.50, 0.02);

        Assert.Null(HoldOutlineBoolean.Merge(a, b, Aspect, maxGap: 0.05));
    }

    [Fact]
    public void TheGapIsMeasuredInPixelsNotInPerAxisFractions()
    {
        var a = Square(0.40, 0.50, 0.02);
        var b = Square(0.45, 0.50, 0.02);

        // 0.01 of the width apart on a 4:3 photo is 0.0133 of the height.
        Assert.Equal(0.01 * Aspect, HoldOutlineBoolean.Gap(a, b, Aspect), 4);
    }

    [Fact]
    public void AHoleAwayFromTheOtherHoldIsKept_AndAPocketTheOtherHoldSitsInIsFilled()
    {
        var pocket = Circle(0.50, 0.50, 0.012, 24);
        var away = Circle(0.47, 0.47, 0.006, 16);
        var host = new OutlineRings(Circle(0.50, 0.50, 0.05, 48), [pocket, away]);
        var inPocket = new OutlineRings(Circle(0.50, 0.50, 0.006, 16), []);

        var merged = HoldOutlineBoolean.Merge(host, inPocket, 1.0, maxGap: 0.05)!;

        Assert.False(merged.Bridged);
        Assert.Single(merged.Shape.Holes);
        Assert.True(merged.Shape.Covers(new P2(0.50, 0.50)));
        Assert.False(merged.Shape.Covers(new P2(0.47, 0.47)));
    }

    [Fact]
    public void RealNeighbouringHoldsOfTheAtticMergeIntoOneOutlineThatCoversBoth()
    {
        var holds = AtticRealData.HoldPanels()[0].Where(h => h.ShapePoints is { Count: >= 3 } && !h.IsVirtual).ToList();
        var pairs = NeighbourPairs(holds, maxGap: 0.012).Take(40).ToList();
        Assert.True(pairs.Count >= 10, $"only {pairs.Count} close pairs");

        foreach (var (a, b) in pairs)
        {
            var ra = OutlineRings.Of(a);
            var rb = OutlineRings.Of(b);
            var merged = HoldOutlineBoolean.Merge(ra, rb, AtticRealData.PhotoAspect, maxGap: 0.05);

            Assert.NotNull(merged);
            Assert.True(ShapeGeometry.IsSimple(merged!.Shape.Outer), $"{a.Id}/{b.Id} is not simple");
            Assert.True(merged.Shape.Area >= (Math.Max(ra.Area, rb.Area) * 0.98), $"{a.Id}/{b.Id} lost area");
            Assert.True(merged.Shape.Covers(ra.Centroid) && merged.Shape.Covers(rb.Centroid), $"{a.Id}/{b.Id} lost a hold");
        }
    }

    [Fact]
    public void AStraightCutSplitsTheHoldInTwoPiecesThatTileIt()
    {
        var shape = Blob(0.50, 0.50, 0.04);

        var cut = HoldOutlineBoolean.Split(shape, [new P2(0.44, 0.40), new P2(0.53, 0.62)], Aspect);

        Assert.Null(cut.Refusal);
        Assert.True(cut.Larger!.Area >= cut.Smaller!.Area);
        Assert.Equal(shape.Area, cut.Larger.Area + cut.Smaller.Area, 5);
    }

    [Fact]
    public void AFreehandCutThatWigglesStillTilesTheHold()
    {
        var shape = Blob(0.50, 0.50, 0.04, phase: 0.4);
        var stroke = Enumerable.Range(0, 40)
            .Select(i => new P2(0.50 + (0.012 * Math.Sin(i * 0.5)), 0.42 + (i * 0.0045)))
            .ToList();

        var cut = HoldOutlineBoolean.Split(shape, stroke, Aspect);

        Assert.Null(cut.Refusal);
        Assert.Equal(shape.Area, cut.Larger!.Area + cut.Smaller!.Area, 5);
        Assert.InRange(cut.Smaller.Area / shape.Area, 0.2, 0.5);
    }

    [Fact]
    public void ACutThatDoesNotCrossTheWholeHoldIsRefused()
    {
        var shape = Blob(0.50, 0.50, 0.04);

        var cut = HoldOutlineBoolean.Split(shape, [new P2(0.40, 0.50), new P2(0.50, 0.50)], Aspect);

        Assert.NotNull(cut.Refusal);
        Assert.Null(cut.Larger);
    }

    [Fact]
    public void ACutThatShavesOffASliverIsRefused()
    {
        var shape = Square(0.50, 0.50, 0.04);

        var cut = HoldOutlineBoolean.Split(shape, [new P2(0.40, 0.461), new P2(0.60, 0.461)], Aspect);

        Assert.Contains("tiny", cut.Refusal);
    }

    [Fact]
    public void HolesGoWithThePartTheyAreIn_AndACutThroughAHoleLeavesItOpen()
    {
        var inLeft = Circle(0.47, 0.50, 0.008, 16);
        var onCut = Circle(0.50, 0.53, 0.008, 16);
        var shape = new OutlineRings(SquareRing(0.50, 0.50, 0.05), [inLeft, onCut]);

        var cut = HoldOutlineBoolean.Split(shape, [new P2(0.50, 0.40), new P2(0.50, 0.60)], 1.0);

        Assert.Null(cut.Refusal);
        var left = new[] { cut.Larger!, cut.Smaller! }.Single(p => p.Centroid.X < 0.50);
        Assert.Single(left.Holes);
        Assert.False(left.Covers(new P2(0.47, 0.50)));
        Assert.False(cut.Larger.Covers(new P2(0.50, 0.53)) || cut.Smaller.Covers(new P2(0.50, 0.53)));
        double expected = shape.Area;
        Assert.Equal(expected, cut.Larger.Area + cut.Smaller.Area, 4);
    }

    [Fact]
    public void EveryRealAtticHoldCanBeHalvedThroughItsCentre()
    {
        var holds = AtticRealData.HoldPanels()[0]
            .Where(h => h.ShapePoints is { Count: >= 3 } && Math.Sqrt(OutlineRings.Of(h).Area / Math.PI) > 0.006)
            .Take(60)
            .ToList();
        Assert.True(holds.Count >= 30);
        foreach (var hold in holds)
        {
            var shape = OutlineRings.Of(hold);
            var c = shape.Centroid;
            var cut = HoldOutlineBoolean.Split(shape, [new P2(c.X - 0.2, c.Y - 0.001), new P2(c.X + 0.2, c.Y + 0.001)], AtticRealData.PhotoAspect);

            Assert.True(cut.Refusal is null, $"{hold.Id}: {cut.Refusal}");
            Assert.Equal(shape.Area, cut.Larger!.Area + cut.Smaller!.Area, 4);
        }
    }

    internal static OutlineRings Blob(double x, double y, double r, double phase = 0) =>
        new(Enumerable.Range(0, 40).Select(i =>
        {
            double t = 2 * Math.PI * i / 40;
            double rr = r * (1 + (0.15 * Math.Sin((3 * t) + phase)) + (0.07 * Math.Cos((5 * t) - phase)));
            return new P2(x + (rr * Math.Cos(t) / Aspect), y + (rr * Math.Sin(t)));
        }).ToList(), []);

    private static OutlineRings Square(double x, double y, double h) => new(SquareRing(x, y, h), []);

    private static List<P2> SquareRing(double x, double y, double h) =>
        [new(x - h, y - h), new(x + h, y - h), new(x + h, y + h), new(x - h, y + h)];

    private static List<P2> Circle(double x, double y, double r, int n) =>
        Enumerable.Range(0, n).Select(i => new P2(x + (r * Math.Cos(2 * Math.PI * i / n)), y + (r * Math.Sin(2 * Math.PI * i / n)))).ToList();

    private static void AssertCovers(OutlineRings merged, OutlineRings part)
    {
        var inside = part.Outer.Count(p => merged.Covers(p) || (ShapeGeometry.ClosestOnBoundary(p, merged.Outer) - p).Length < 5e-4);
        Assert.True(inside >= part.Outer.Count - 1, $"{part.Outer.Count - inside} vertices fell outside the merge");
    }

    private static IEnumerable<(Hold A, Hold B)> NeighbourPairs(List<Hold> holds, double maxGap)
    {
        for (int i = 0; i < holds.Count; i++)
        {
            for (int j = i + 1; j < holds.Count; j++)
            {
                double gap = HoldOutlineBoolean.Gap(OutlineRings.Of(holds[i]), OutlineRings.Of(holds[j]), AtticRealData.PhotoAspect);
                if (gap is > 0.0005 && gap < maxGap)
                {
                    yield return (holds[i], holds[j]);
                }
            }
        }
    }
}
