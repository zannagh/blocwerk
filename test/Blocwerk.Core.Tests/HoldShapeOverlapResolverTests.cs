using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Tests;

/// <summary>Overlap resolution on one panel: clip, else circle, else smaller circle; locked holds always win.</summary>
public sealed class HoldShapeOverlapResolverTests
{
    private static readonly Guid A = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = new("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = new("00000000-0000-0000-0000-00000000000c");

    [Fact]
    public void SeparateShapesAreUnchanged()
    {
        var result = HoldShapeOverlapResolver.Resolve([Auto(A, 0.2, 0.5), Auto(B, 0.6, 0.5)]);

        Assert.All(result, r => Assert.Equal(HoldShapeFit.Unchanged, r.Fit));
    }

    [Fact]
    public void TouchingBlobsEndUpWithAGapAndNoOverlap()
    {
        // Two 0.05-radius blobs, centres 0.09 apart: they overlap by 0.01.
        var a = Auto(A, 0.30, 0.5);
        var b = Auto(B, 0.39, 0.5);

        var result = HoldShapeOverlapResolver.Resolve([a, b]);

        var ra = result.Single(r => r.Id == A);
        var rb = result.Single(r => r.Id == B);
        Assert.Equal(HoldShapeFit.Unchanged, ra.Fit);
        Assert.Equal(HoldShapeFit.Shrunk, rb.Fit);
        Assert.NotNull(rb.Shape);
        Assert.True(Gap(a, ra, b, rb) >= HoldShapeOverlapResolver.Tolerance - 1e-4);
        Assert.True(HoldShapeSmoother.IsSmooth(rb.Shape));
    }

    [Fact]
    public void ALockedShapeWinsAndIsNeverChanged()
    {
        var manual = Auto(A, 0.30, 0.5) with { Locked = true };
        var auto = Auto(B, 0.37, 0.5);

        var result = HoldShapeOverlapResolver.Resolve([manual, auto]);

        var only = Assert.Single(result);
        Assert.Equal(B, only.Id);
        Assert.NotEqual(HoldShapeFit.Unchanged, only.Fit);
        Assert.True(Gap(manual, new HoldShapeResolution(A, HoldShapeFit.Unchanged, manual.Shape, manual.Radius), auto, only) >= HoldShapeOverlapResolver.Tolerance - 1e-4);
    }

    [Fact]
    public void WhenNoOutlineFitsTheHoldBecomesItsPlainCircle()
    {
        // A wide flat outline pinned between two locked holds: it cannot keep half its area, but the
        // plain circle (which is narrower than the outline) fits between them.
        var wide = new HoldShapeInput(
            A,
            0.5,
            0.5,
            0.02,
            [new() { Dx = -0.08, Dy = -0.01 }, new() { Dx = 0.08, Dy = -0.01 }, new() { Dx = 0.08, Dy = 0.01 }, new() { Dx = -0.08, Dy = 0.01 }],
            false);
        var left = new HoldShapeInput(B, 0.4348, 0.5, 0.025, null, true);
        var right = new HoldShapeInput(C, 0.5652, 0.5, 0.025, null, true);

        var r = Assert.Single(HoldShapeOverlapResolver.Resolve([wide, left, right]));

        Assert.Equal(HoldShapeFit.Circle, r.Fit);
        Assert.Null(r.Shape);
        Assert.Equal(0.02, r.Radius);
    }

    [Fact]
    public void WhenTheCircleOverlapsTooTheRadiusShrinksButNotBelowTheMinimum()
    {
        var hold = new HoldShapeInput(A, 0.5, 0.5, 0.04, null, false);
        var neighbour = new HoldShapeInput(B, 0.565, 0.5, 0.04, null, true);

        var r = Assert.Single(HoldShapeOverlapResolver.Resolve([hold, neighbour]));

        Assert.Equal(HoldShapeFit.ShrunkCircle, r.Fit);
        Assert.InRange(r.Radius, 0.04 * HoldShapeOverlapResolver.MinRadiusFraction, 0.04);
        Assert.True(Gap(hold, new HoldShapeResolution(A, r.Fit, null, r.Radius), neighbour, new HoldShapeResolution(B, HoldShapeFit.Unchanged, null, 0.04)) >= HoldShapeOverlapResolver.Tolerance - 1e-4);
    }

    [Fact]
    public void ACircleThatCannotFitStaysAtTheMinimumAndIsReportedUnresolved()
    {
        var hold = new HoldShapeInput(A, 0.5, 0.5, 0.04, null, false);
        var onTop = new HoldShapeInput(B, 0.505, 0.5, 0.04, null, true);

        var r = Assert.Single(HoldShapeOverlapResolver.Resolve([hold, onTop]));

        Assert.Equal(HoldShapeFit.Unresolved, r.Fit);
        Assert.Equal(0.04 * HoldShapeOverlapResolver.MinRadiusFraction, r.Radius, 9);
    }

    [Fact]
    public void RadiusShrinkCanBeSwitchedOff()
    {
        var hold = new HoldShapeInput(A, 0.5, 0.5, 0.04, null, false);
        var neighbour = new HoldShapeInput(B, 0.565, 0.5, 0.04, null, true);

        var r = Assert.Single(HoldShapeOverlapResolver.Resolve([hold, neighbour], allowRadiusShrink: false));

        Assert.Equal(0.04, r.Radius);
        Assert.Equal(HoldShapeFit.Unresolved, r.Fit);
    }

    [Fact]
    public void TheResultDoesNotDependOnInputOrder()
    {
        var holds = new[] { Auto(C, 0.45, 0.5), Auto(A, 0.30, 0.5), Auto(B, 0.38, 0.5) };

        var forward = HoldShapeOverlapResolver.Resolve(holds);
        var backward = HoldShapeOverlapResolver.Resolve(holds.Reverse().ToList());

        Assert.Equal(Describe(forward), Describe(backward));
        Assert.Equal(Describe(forward), Describe(HoldShapeOverlapResolver.Resolve(holds)));
        Assert.Equal(new[] { A, B, C }, forward.Select(r => r.Id));
    }

    [Fact]
    public void ThreeOverlappingBlobsEndUpPairwiseApart()
    {
        var holds = new[] { Auto(A, 0.30, 0.5), Auto(B, 0.37, 0.5), Auto(C, 0.34, 0.56) };

        var result = HoldShapeOverlapResolver.Resolve(holds);

        for (int i = 0; i < holds.Length; i++)
        {
            for (int j = i + 1; j < holds.Length; j++)
            {
                var ri = result.Single(r => r.Id == holds[i].Id);
                var rj = result.Single(r => r.Id == holds[j].Id);
                Assert.True(Gap(holds[i], ri, holds[j], rj) >= HoldShapeOverlapResolver.Tolerance - 1e-4, $"{i}/{j}");
            }
        }
    }

    private static HoldShapeInput Auto(Guid id, double x, double y) =>
        new(id, x, y, 0.05, HoldShapeSmootherTests.Blob(0.05, 0.05, 24), false);

    private static string Describe(IEnumerable<HoldShapeResolution> rs) =>
        string.Join("|", rs.Select(r => $"{r.Id}:{r.Fit}:{r.Radius:R}:{string.Join(",", (r.Shape ?? []).Select(p => $"{p.Dx:R}/{p.Dy:R}"))}"));

    private static double Gap(HoldShapeInput a, HoldShapeResolution ra, HoldShapeInput b, HoldShapeResolution rb) =>
        Distance(Dense(a, ra.Shape, ra.Radius), Dense(b, rb.Shape, rb.Radius));

    /// <summary>The boundary as a dense point cloud (edges sampled every ~0.0005), independent of the production code.</summary>
    private static List<(double X, double Y)> Dense(HoldShapeInput h, IReadOnlyList<ShapePoint>? shape, double radius)
    {
        var ring = shape is { Count: >= 3 }
            ? shape.Select(s => (X: h.X + s.Dx, Y: h.Y + s.Dy)).ToList()
            : Enumerable.Range(0, 720).Select(i => (X: h.X + (radius * Math.Cos(i * Math.PI / 360)), Y: h.Y + (radius * Math.Sin(i * Math.PI / 360)))).ToList();
        var pts = new List<(double X, double Y)>();
        for (int i = 0; i < ring.Count; i++)
        {
            var p = ring[i];
            var q = ring[(i + 1) % ring.Count];
            int parts = Math.Max(1, (int)(Math.Sqrt(Math.Pow(q.X - p.X, 2) + Math.Pow(q.Y - p.Y, 2)) / 0.0005));
            for (int k = 0; k < parts; k++)
            {
                pts.Add((p.X + ((q.X - p.X) * k / parts), p.Y + ((q.Y - p.Y) * k / parts)));
            }
        }

        return pts;
    }

    private static double Distance(List<(double X, double Y)> a, List<(double X, double Y)> b)
    {
        double best = double.MaxValue;
        foreach (var p in a)
        {
            foreach (var q in b)
            {
                best = Math.Min(best, Math.Sqrt(Math.Pow(p.X - q.X, 2) + Math.Pow(p.Y - q.Y, 2)));
            }
        }

        return best;
    }
}
