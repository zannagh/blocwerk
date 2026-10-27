// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Surface;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Surface placement on <see cref="SurfaceScene"/>: holds stand 40 mm out, so a plane cast from a steep view lands well off the
/// base; the surface cast dropped along the normal lands on it from any view, and linked copies share the head-on view's result.
/// </summary>
public class SurfaceHoldPlannerTests
{
    private static readonly Guid PanelA = Guid.NewGuid();
    private static readonly Guid PanelB = Guid.NewGuid();
    private static readonly SolvedCamera HeadOn = SurfaceScene.LookAt("a", [900, -3000, 1100], [800, 0, 1200]);
    private static readonly SolvedCamera Steep = SurfaceScene.LookAt("b", [2600, -2200, 400], [800, 0, 1200]);

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public void Cast_FindsTheBase_FromAnyView(string view)
    {
        var camera = view == "a" ? HeadOn : Steep;
        var hold = SurfaceScene.HoldSeenBy(camera, PanelA, 800, 1200);

        var cast = SurfaceCaster.Cast(hold, camera, Facets(), Surface((800, 1200)))!;

        Assert.Equal("0", cast.FacetId);
        Assert.Equal(800, cast.A, 4.0);
        Assert.Equal(1200, cast.B, 4.0);
    }

    [Fact]
    public void PlaneCast_FromASteepView_IsOffByTheParallax_TheSurfaceCastRemovesIt()
    {
        var hold = SurfaceScene.HoldSeenBy(Steep, PanelA, 800, 1200);
        var (a, b) = Steep.PixelToPlane(Facets()["0"].Frame, hold.X * 1000, hold.Y * 1000)!.Value;

        Assert.True(Math.Sqrt(((a - 800) * (a - 800)) + ((b - 1200) * (b - 1200))) > 30);
    }

    [Fact]
    public void Cast_MissesWhereThereIsNoSurface()
    {
        var hold = SurfaceScene.HoldSeenBy(HeadOn, PanelA, 1700, 2400);

        Assert.Null(SurfaceCaster.Cast(hold, HeadOn, Facets(), Surface((800, 1200))));
    }

    [Fact]
    public void Plan_GivesLinkedCopiesTheHeadOnPosition_AndEachItsOwnFootprint()
    {
        var onA = SurfaceScene.HoldSeenBy(HeadOn, PanelA, 800, 1200);
        var onB = SurfaceScene.HoldSeenBy(Steep, PanelB, 800, 1200);
        onB.PlaneAMm = 900;
        var alone = SurfaceScene.HoldSeenBy(Steep, PanelB, 1100, 900);

        var plan = Plan([onA, onB, alone], [new HoldLinkPair(onA.Id, onB.Id)], Surface((800, 1200), (1100, 900)));

        var a = plan.Fixes.Single(f => f.Hold.Id == onA.Id);
        var b = plan.Fixes.Single(f => f.Hold.Id == onB.Id);
        Assert.Equal((a.FacetId, a.A, a.B), (b.FacetId, b.A, b.B));
        Assert.All(new[] { a, b }, f => Assert.Equal(HoldFootprint.KeyOf(f.Hold), f.Footprint!.OutlineKey));
        Assert.Equal(HoldMetric.Surface, a.Size!.MetricSource);
        Assert.Equal(1100, plan.Fixes.Single(f => f.Hold.Id == alone.Id).A, 4.0);
        Assert.True(Assert.Single(plan.PairGapsMm) < 8);
        var panelB = plan.Panels.Single(p => p.PanelId == PanelB);
        Assert.Equal((2, 1, 1, 0, 0), (panelB.Checked, panelB.OwnView, panelB.SharedView, panelB.Fallback, panelB.Missed));
    }

    [Fact]
    public void Plan_KeepsEachOwnPosition_WhenALinkJoinsTwoDifferentHolds()
    {
        var onA = SurfaceScene.HoldSeenBy(HeadOn, PanelA, 800, 1200);
        var other = SurfaceScene.HoldSeenBy(Steep, PanelB, 1100, 900);

        var plan = Plan([onA, other], [new HoldLinkPair(onA.Id, other.Id)], Surface((800, 1200), (1100, 900)));

        Assert.Equal(1100, plan.Fixes.Single(f => f.Hold.Id == other.Id).A, 4.0);
        Assert.Equal(0, plan.Panels.Sum(p => p.SharedView));
    }

    [Fact]
    public void Plan_LeavesAHoldNoViewCanCast_AndCountsIt()
    {
        var lost = SurfaceScene.HoldSeenBy(HeadOn, PanelA, 1600, 2500);

        var plan = Plan([lost], [], Surface((800, 1200)));

        Assert.Empty(plan.Fixes);
        Assert.Equal(1, plan.Panels.Single().Missed);
    }

    private static SurfacePlan Plan(List<Entities.Hold> holds, List<HoldLinkPair> links, SplatSurface surface) =>
        SurfaceHoldPlanner.Plan(holds, links, new Dictionary<Guid, SolvedCamera> { [PanelA] = HeadOn, [PanelB] = Steep }, Facets(), surface);

    /// <summary>The wall around the scene's holds (a hole elsewhere, so a hold there has no surface).</summary>
    private static SplatSurface Surface(params (double A, double B)[] holds) =>
        new(SurfaceScene.Points(500, 1400, 600, 1500, holds));

    private static Dictionary<string, (FacetFrame Frame, PlaneRectMm Extent)> Facets()
    {
        var doc = WallGeometryDocument.Parse(HoldPlacementScenario.TwoFacetJson);
        return doc.Segments.SelectMany(s => s.Facets).ToDictionary(
            f => f.Id!, f => (FacetFrame.From(f)!, f.ExtentMm!.Value));
    }
}
