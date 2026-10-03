using System.Diagnostics;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Tests;

/// <summary>A dense synthetic panel (900 holds, ~half of them overlapping): fast, overlap-free and stable on re-run.</summary>
public sealed class HoldShapeCleanupStressTests
{
    [Fact]
    public void ADensePanelIsPlannedQuicklyAndEndsWithoutAutoOverlaps()
    {
        var holds = DensePanel(seed: 42, count: 900);

        var sw = Stopwatch.StartNew();
        var plan = HoldShapeCleanup.PlanDetailed(holds, 1.33);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"planning took {sw.Elapsed.TotalSeconds:F1}s");
        Assert.NotEmpty(plan.Changes);
        Apply(holds, plan);
        var offenders = OverlappingPairs(holds).Where(p => IsAuto(p.A) && IsAuto(p.B)).ToList();
        var unresolved = plan.Unresolved.ToHashSet();
        Assert.All(offenders, p => Assert.True(unresolved.Contains(p.A.Id) || unresolved.Contains(p.B.Id), $"{p.A.Id} x {p.B.Id}"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ARerunOnACleanedPanelChangesNothing(int seed)
    {
        var holds = DensePanel(seed, count: 400);
        Apply(holds, HoldShapeCleanup.PlanDetailed(holds, 1.33));

        var second = HoldShapeCleanup.PlanDetailed(holds, 1.33);

        Assert.Empty(second.Changes);
    }

    [Fact]
    public void LockedHoldsAreNeverChanged()
    {
        var holds = DensePanel(seed: 5, count: 400);
        var before = holds.Where(h => !HoldShapeCleanup.IsResolvable(h)).ToDictionary(h => h.Id, Key);

        Apply(holds, HoldShapeCleanup.PlanDetailed(holds, 1.33));

        Assert.All(holds.Where(h => !HoldShapeCleanup.IsResolvable(h)), h => Assert.Equal(before[h.Id], Key(h)));
    }

    [Fact]
    public void ACircleIsShrunkToTheLargestRadiusThatClears()
    {
        var manual = new Hold { Id = new Guid(1, 0, 0, new byte[8]), X = 0.5, Y = 0.5, Radius = 0.04, IsAutoDetected = false };
        var auto = new Hold { Id = new Guid(2, 0, 0, new byte[8]), X = 0.57, Y = 0.5, Radius = 0.04, IsAutoDetected = true, OutlineSource = HoldOutlineSource.AutoCircle };

        var change = Assert.Single(HoldShapeCleanup.Plan([manual, auto]));

        Assert.Equal(HoldShapeChangeKind.ShrunkCircle, change.Kind);
        double gap = 0.07 - 0.04 - change.Radius;
        Assert.InRange(gap, HoldShapeOverlapResolver.Tolerance - 1e-4, HoldShapeOverlapResolver.Tolerance + 0.002);
    }

    [Fact]
    public void AHoleOutsideTheSmoothedOutlineIsDropped()
    {
        var shape = HoldShapeSmootherTests.Blob(0.03, 0.03, 24);
        var inside = new List<ShapePoint> { new() { Dx = 0, Dy = 0 }, new() { Dx = 0.01, Dy = 0 }, new() { Dx = 0, Dy = 0.01 } };
        var outside = new List<ShapePoint> { new() { Dx = 0.05, Dy = 0 }, new() { Dx = 0.06, Dy = 0 }, new() { Dx = 0.05, Dy = 0.01 } };

        var kept = HoldShapeHoles.Inside([inside, outside], shape);

        var ring = Assert.Single(kept!);
        Assert.Equal(inside.Select(p => p.Dx), ring.Select(p => p.Dx));
        Assert.Null(HoldShapeHoles.Inside([outside], shape));
    }

    private static string Key(Hold h) =>
        $"{h.Radius:R}|{string.Join(",", (h.ShapePoints ?? []).Select(p => $"{p.Dx:R}/{p.Dy:R}"))}";

    private static bool IsAuto(Hold h) => HoldShapeCleanup.IsResolvable(h);

    private static void Apply(List<Hold> holds, HoldShapePlan plan)
    {
        foreach (var c in plan.Changes)
        {
            var h = holds.Single(x => x.Id == c.HoldId);
            h.Radius = c.Radius;
            h.ShapePoints = c.Shape;
        }
    }

    private static List<(Hold A, Hold B)> OverlappingPairs(List<Hold> holds)
    {
        var polys = holds.Select(h => (Hold: h, Poly: Polygon(h))).ToList();
        var pairs = new List<(Hold, Hold)>();
        for (int i = 0; i < polys.Count; i++)
        {
            for (int j = i + 1; j < polys.Count; j++)
            {
                if (Math.Abs(polys[i].Hold.X - polys[j].Hold.X) < 0.1 && Math.Abs(polys[i].Hold.Y - polys[j].Hold.Y) < 0.1
                    && ShapeGeometry.Distance(polys[i].Poly, polys[j].Poly) < HoldShapeOverlapResolver.Tolerance * 0.5)
                {
                    pairs.Add((polys[i].Hold, polys[j].Hold));
                }
            }
        }

        return pairs;
    }

    private static P2[] Polygon(Hold h) =>
        h.ShapePoints is { Count: >= 3 } shape
            ? shape.Select(s => new P2(h.X + s.Dx, h.Y + s.Dy)).ToArray()
            : ShapeObstacles.Circle(new P2(h.X, h.Y), h.Radius);

    private static List<Hold> DensePanel(int seed, int count)
    {
        var random = new Random(seed);
        var holds = new List<Hold>();
        for (int i = 0; i < count; i++)
        {
            double radius = 0.012 + (random.NextDouble() * 0.02);
            var kind = random.NextDouble();
            var hold = new Hold
            {
                Id = new Guid(i + 1, 0, 0, new byte[8]),
                X = 0.03 + (random.NextDouble() * 0.94),
                Y = 0.03 + (random.NextDouble() * 0.94),
                Radius = radius,
                IsAutoDetected = kind < 0.85,
                OutlineSource = kind < 0.55 ? HoldOutlineSource.AutoContour : kind < 0.85 ? HoldOutlineSource.AutoCircle : HoldOutlineSource.Manual,
            };
            if (kind < 0.55 || (kind >= 0.85 && kind < 0.93))
            {
                hold.ShapePoints = RoughShape(random, radius * 1.3);
            }

            holds.Add(hold);
        }

        return holds;
    }

    private static List<ShapePoint> RoughShape(Random random, double radius)
    {
        int n = 14 + random.Next(12);
        var points = new List<ShapePoint>();
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            double r = radius * (0.85 + (random.NextDouble() * 0.3));
            if (random.NextDouble() < 0.12)
            {
                r *= random.NextDouble() < 0.5 ? 1.8 : 0.35;
            }

            points.Add(new ShapePoint { Dx = Math.Round(r * Math.Cos(a), 5), Dy = Math.Round(r * Math.Sin(a), 5) });
        }

        return points;
    }
}
