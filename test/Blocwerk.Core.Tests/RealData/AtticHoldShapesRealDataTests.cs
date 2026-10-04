// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Tests.RealData;

/// <summary>
/// The hold shape clean-up on The Attic's real live holds (895 on two panels: traced outlines, auto circles, manual holds):
/// afterwards no two automatic holds overlap except the known unresolvable ones, a re-run changes nothing, and manual
/// holds are never touched.
/// </summary>
public sealed class AtticHoldShapesRealDataTests
{
    [Fact]
    public void TheFixtureHasTheTwoLivePanels_WithEightHundredNinetyFiveHolds()
    {
        var panels = AtticRealData.HoldPanels();

        Assert.Equal([601, 294], panels.Select(p => p.Count));
        Assert.Equal(895, panels.Sum(p => p.Count));
    }

    [Fact]
    public void AfterTheCleanUp_NoTwoAutomaticHoldsOverlap_ExceptTheKnownUnresolvableOnes()
    {
        var unresolved = 0;
        var overlapping = 0;
        foreach (var holds in AtticRealData.HoldPanels())
        {
            var plan = HoldShapeCleanup.PlanDetailed(holds, AtticRealData.PhotoAspect);
            Apply(holds, plan);
            var offenders = OverlappingPairs(holds).Where(p => IsAuto(p.A) && IsAuto(p.B)).ToList();

            // Every remaining overlap involves a hold the plan itself reports as unresolved.
            var known = plan.Unresolved.ToHashSet();
            Assert.All(offenders, p => Assert.True(known.Contains(p.A.Id) || known.Contains(p.B.Id), $"{p.A.Id} x {p.B.Id}"));
            unresolved += plan.Unresolved.Count;
            overlapping += offenders.Count;
        }

        // The snapshot: 37 holds the plan cannot clear (jammed between locked manual holds and each other) and, among them,
        // 12 pairs of automatic holds that still touch. A change here is a change of the clean-up on the real wall: look at
        // it before updating the numbers (the dense synthetic test cannot show a regression on real holds).
        Assert.Equal((37, 12), (unresolved, overlapping));
    }

    [Fact]
    public void AReRunOnTheCleanedPanels_ChangesNothing()
    {
        foreach (var holds in AtticRealData.HoldPanels())
        {
            Apply(holds, HoldShapeCleanup.PlanDetailed(holds, AtticRealData.PhotoAspect));

            Assert.Empty(HoldShapeCleanup.PlanDetailed(holds, AtticRealData.PhotoAspect).Changes);
        }
    }

    [Fact]
    public void ManualAndVirtualHolds_AreNeverChanged()
    {
        foreach (var holds in AtticRealData.HoldPanels())
        {
            var before = holds.Where(h => !HoldShapeCleanup.IsResolvable(h)).ToDictionary(h => h.Id, Key);

            Apply(holds, HoldShapeCleanup.PlanDetailed(holds, AtticRealData.PhotoAspect));

            Assert.NotEmpty(before);
            Assert.All(holds.Where(h => !HoldShapeCleanup.IsResolvable(h)), h => Assert.Equal(before[h.Id], Key(h)));
        }
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
        for (var i = 0; i < polys.Count; i++)
        {
            for (var j = i + 1; j < polys.Count; j++)
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
}
