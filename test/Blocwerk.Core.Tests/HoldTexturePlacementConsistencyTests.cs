// <copyright file="HoldTexturePlacementConsistencyTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The consistency check of a run's placements: two overlapping photos placing a linked hold at different spots
/// (the weaker registration loses), and a hold placed far beyond its photo's matches without a twin to confirm it.
/// </summary>
public class HoldTexturePlacementConsistencyTests
{
    [Fact]
    public async Task TwinsThatDisagree_KeepTheBetterRegistrationsPlacement_AndTheOtherIsNotMeasured()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);

        // Photo c1 sees facet 0 sparser than c0 (140 vs 560 inliers), mapped 500 mm to the right: a = x + 500.
        s.Matcher.Views.Add(new FakeTextureView(20, 0, 0, 2000, 200, HoldPlacementScenario.Shift(99.5 + 500)));
        var onC0 = await s.AddHoldAsync(1000 / 4000.0, 0.5);
        var onC1 = await s.AddHoldAsync(800 / 4000.0, 0.5, s.PanelC1);
        await LinkAsync(h, onC0, onC1);

        var result = await s.Service().PlaceAsync(h.WallId);

        var holds = await s.LoadHoldsAsync();
        Assert.Equal("0", holds[onC0].FacetId);
        Assert.Equal(1000, holds[onC0].PlaneAMm!.Value, 1);
        Assert.Null(holds[onC1].FacetId);
        Assert.Equal(HoldMetric.TextureRegistrationRejected, holds[onC1].MetricSource);
        var c1 = result.Panels.Single(p => p.PanelId == s.PanelC1);
        Assert.Equal((0, 1, 1, 0), (c1.Placed, c1.Failed, c1.Disagreed, c1.Unsupported));
        Assert.Equal(0, result.Panels.Single(p => p.PanelId == s.PanelC0).Disagreed);
        Assert.Equal("1 hold left unmeasured because the two photos disagree.", HoldPlacementUnmeasured.Text(result.Panels));

        await s.Service().RevertAsync(h.WallId, result.RunId);
        Assert.Null((await s.LoadHoldsAsync())[onC1].MetricSource);
    }

    [Fact]
    public async Task TwinsThatAgree_AreBothPlaced()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        s.Matcher.Views.Add(new FakeTextureView(20, 0, 0, 2000, 200, HoldPlacementScenario.Shift(99.5 + 500)));
        var onC0 = await s.AddHoldAsync(1000 / 4000.0, 0.5);
        var onC1 = await s.AddHoldAsync(500 / 4000.0, 0.5, s.PanelC1);
        await LinkAsync(h, onC0, onC1);

        var result = await s.Service().PlaceAsync(h.WallId);

        var holds = await s.LoadHoldsAsync();
        Assert.Equal(("0", "0"), (holds[onC0].FacetId, holds[onC1].FacetId));
        Assert.Equal(1000, holds[onC1].PlaneAMm!.Value, 1);
        Assert.All(result.Panels, p => Assert.Equal((0, 0), (p.Disagreed, p.Unsupported)));
        Assert.Equal(string.Empty, HoldPlacementUnmeasured.Text(result.Panels));
    }

    [Fact]
    public async Task AHoldFarBeyondThePhotosMatches_IsNotMeasured_UnlessItsTwinConfirmsIt()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);

        // Photo c1 matches facet 0 only on its left 600 px (a ∈ [25, 575] mm), yet the fit covers the whole facet.
        s.Matcher.Views.Add(new FakeTextureView(20, 0, 0, 600, 50, HoldPlacementScenario.Shift(99.5)));
        var near = await s.AddHoldAsync(300 / 4000.0, 0.5, s.PanelC1);
        var far = await s.AddHoldAsync(1800 / 4000.0, 0.5, s.PanelC1);
        var confirmed = await s.AddHoldAsync(1500 / 4000.0, 0.5, s.PanelC1);
        await LinkAsync(h, await s.AddHoldAsync(1500 / 4000.0, 0.5), confirmed);

        var result = await s.Service().PlaceAsync(h.WallId);

        var holds = await s.LoadHoldsAsync();
        Assert.Equal("0", holds[near].FacetId);
        Assert.Equal("0", holds[confirmed].FacetId);
        Assert.Null(holds[far].FacetId);
        var c1 = result.Panels.Single(p => p.PanelId == s.PanelC1);
        Assert.Equal((2, 1, 0, 1), (c1.Placed, c1.Failed, c1.Disagreed, c1.Unsupported));
    }

    [Fact]
    public async Task TwoRegistrationsOfOnePhotoThatDisagree_TheOneWithTheStrongerLocalEvidencePlacesTheHold()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);

        // Photo c1 registers facet 0 densely over its left half (a = x) and, wrongly, facet 1 sparsely over x ∈ [1000, 2000].
        s.Matcher.Views.Add(new FakeTextureView(20, 0, 0, 2000, 50, HoldPlacementScenario.Shift(99.5)));
        s.Matcher.Views.Add(new FakeTextureView(20, 1, 1000, 2000, 200, HoldPlacementScenario.Shift(99.5 - 1000)));
        var hold = await s.AddHoldAsync(1500 / 4000.0, 0.5, s.PanelC1, x => x.FacetId = "1");

        var result = await s.Service().PlaceAsync(h.WallId);

        var placed = (await s.LoadHoldsAsync())[hold];
        Assert.Equal("0", placed.FacetId);
        Assert.Equal(1500, placed.PlaneAMm!.Value, 1);
        Assert.Equal((1, 0), (result.Panels.Single(p => p.PanelId == s.PanelC1).Placed, result.Panels.Single(p => p.PanelId == s.PanelC1).Failed));
    }

    [Fact]
    public void Judge_WithoutAClearWinner_DropsBoth_AndSupportMeasuresTheDistanceBeyondTheInliers()
    {
        // Normalised photo → plane: a = 2000 x, b = 3000 − 3000 y; inliers on the left half of the photo.
        var map = PlaneHomography.FromCoefficients([2000, 0, 0, 0, -3000, 3000, 0, 0, 1]);
        (double X, double Y)[] inliers = [(0.1, 0.1), (0.4, 0.1), (0.4, 0.9), (0.1, 0.9)];
        var r = new FacetRegistration("0", true, 100, 90, 50, 0.5, 0.5, 2, null, map, new PlaneRectMm(0, 2000, 0, 3000), 1, inliers);
        var support = InlierSupport.Of(r)!;

        Assert.Equal(0, support.BeyondHullMm(500, 1500), 6);
        Assert.Equal(1000, support.BeyondHullMm(1800, 1500), 6);
        Assert.Equal(1000, support.NearestInlierMm(1800, 2700), 6);
        Assert.True(support.IsExtrapolated(1800, 1500));
        Assert.False(support.IsExtrapolated(1200, 1500));

        var first = new TwinSide([0, 0, 0], 80, r, 20, 0);
        Assert.Equal(TwinVerdict.Agree, TwinConsistency.Judge(first, first with { World = [50, 0, 0] }));
        Assert.Equal(TwinVerdict.BothWrong, TwinConsistency.Judge(first, first with { World = [200, 0, 0], NearestInlierMm = 40 }));
        Assert.Equal(TwinVerdict.SecondWrong, TwinConsistency.Judge(first, first with { World = [200, 0, 0], BeyondHullMm = 400 }));
        Assert.Equal(TwinVerdict.FirstWrong, TwinConsistency.Judge(first with { NearestInlierMm = 300 }, first with { World = [200, 0, 0] }));
    }

    [Fact]
    public void TheView_GuessesNoPositionForAHoldARunLeftUnmeasured()
    {
        var panelId = Guid.NewGuid();
        var wall = new Wall { Name = "Test", CurrentGeneration = 2 };
        for (var i = 0; i < 12; i++)
        {
            var (x, y) = (0.1 + (0.07 * i), 0.15 + (0.06 * (i % 5)));
            wall.Holds.Add(new Hold
            {
                WallId = wall.Id, WallPanelId = panelId, Generation = 2, X = x, Y = y, Radius = 0.02,
                FacetId = "0", PlaneAMm = (1000 * x) + 100, PlaneBMm = 3000 - (1000 * y), WidthMm = 50, HeightMm = 50,
            });
        }

        var rejected = new Hold
        {
            WallId = wall.Id, WallPanelId = panelId, Generation = 2, X = 0.4, Y = 0.4, Radius = 0.02,
            MetricSource = HoldMetric.TextureRegistrationRejected,
        };
        wall.Holds.Add(rejected);

        var view = Wall3DViewBuilder.Build(wall, WallGeometryDocument.Parse(Wall3DViewBuilderTests.SolvedJson), null);

        Assert.Equal(1, view.UnplacedHoldCount);
        Assert.DoesNotContain(view.Holds, x => x.Id == rejected.Id);
    }

    private static async Task LinkAsync(WallTestHarness h, Guid a, Guid b)
    {
        await using var db = h.CreateContext();
        db.HoldLinks.Add(new HoldLink { WallId = h.WallId, HoldAId = a, HoldBId = b });
        await db.SaveChangesAsync();
    }
}
