// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// An edit must never drop a hold out of the 3D view: a reshape keeps its facet position and gets its size
/// back from the edited outline, a move is re-placed in the same save through the photo's placed holds,
/// the stale footprint stays (ignored by its outline key) and is queued for refinement, and the 3D view
/// still draws a hold that has a panel position but no facet position.
/// </summary>
public class HoldEditPlacementTests
{
    [Fact]
    public async Task ShapeEdit_KeepsThePlacement_AndRemeasuresTheSize()
    {
        using var h = new WallTestHarness();
        var (_, edited) = await SeedPlacedPanelAsync(h);
        var queue = Substitute.For<IHoldRefinementQueue>();
        var service = Service(h, queue);
        var outline = ShapePoint.DefaultOctagon(0.03);

        await service.UpdateHoldAsync(edited.Id, Edit(edited.X, edited.Y, 0.03, outline));

        var stored = await LoadAsync(h, edited.Id);
        Assert.Equal("0", stored.FacetId);
        Assert.Equal(edited.PlaneAMm, stored.PlaneAMm);
        Assert.Equal(edited.PlaneBMm, stored.PlaneBMm);
        Assert.Equal("multi-view", stored.MetricSource);
        Assert.Equal(HoldOutlineSource.Manual, stored.OutlineSource);
        Assert.Equal((outline.Max(p => p.Dx) - outline.Min(p => p.Dx)) * 1000, stored.WidthMm!.Value, 1);
        Assert.Equal((outline.Max(p => p.Dy) - outline.Min(p => p.Dy)) * 300, stored.HeightMm!.Value, 1);

        // The footprint is kept, but stale: its outline key no longer matches, so the 3D view projects the outline.
        Assert.NotNull(stored.FootprintMm);
        Assert.Null(HoldFootprint.For(stored));
        queue.Received(1).Enqueue(h.WallId, Arg.Is<IEnumerable<Guid>>(ids => ids.Single() == edited.Id));
    }

    [Fact]
    public void ShapeReviewAdjust_KeepsThePlacement()
    {
        var hold = new Hold
        {
            X = 0.5, Y = 0.5, Radius = 0.02, ShapePoints = ShapePoint.DefaultOctagon(0.02),
            FacetId = "0", PlaneAMm = 600, PlaneBMm = 1100, WidthMm = 40, HeightMm = 12,
        };
        var proposal = new WallUpdateShapeProposal
        {
            Decision = ShapeReviewDecision.Adjusted, AnchorX = 0.5, AnchorY = 0.5,
            AdjustedShapeJson = ShapeJson.Write(ShapePoint.DefaultOctagon(0.03)),
        };

        Assert.True(ShapeDecisionApplier.Apply(hold, proposal));

        Assert.Equal("0", hold.FacetId);
        Assert.Equal(600, hold.PlaneAMm);
        Assert.Null(hold.WidthMm);
    }

    [Fact]
    public async Task PositionEdit_RePlacesTheHold_InTheSameSave()
    {
        using var h = new WallTestHarness();
        var (_, edited) = await SeedPlacedPanelAsync(h);

        await Service(h, null).UpdateHoldAsync(edited.Id, Edit(0.5, 0.5, edited.Radius, edited.ShapePoints));

        var stored = await LoadAsync(h, edited.Id);
        var (a, b) = PlaneOf(0.5, 0.5);
        Assert.Equal("0", stored.FacetId);
        Assert.Equal(a, stored.PlaneAMm!.Value, 1);
        Assert.Equal(b, stored.PlaneBMm!.Value, 1);
        Assert.Equal(HoldMetric.HoldFit, stored.MetricSource);
        Assert.NotNull(stored.WidthMm);
    }

    [Fact]
    public async Task StagedMove_RePlacesTheHold()
    {
        using var h = new WallTestHarness();
        var (panelId, _) = await SeedPlacedPanelAsync(h, generation: 2);
        Guid staged;
        await using (var db = h.CreateContext())
        {
            var panel = await db.WallPanels.SingleAsync(p => p.Id == panelId);
            (panel.Generation, panel.StagedPhoto, panel.StagedPhotoContentType) = (2, [1, 2, 3], "image/jpeg");
            staged = db.Holds.First(x => x.Generation == 2 && x.FootprintMm != null).Id;
            await db.SaveChangesAsync();
        }

        var service = new WallPanelService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, Substitute.For<IHoldOverlapMatcher>(), NullLogger<WallPanelService>.Instance);
        await service.UpdateStagedHoldAsync(h.WallId, staged, 0.45, 0.35, 0.02);

        var stored = await LoadAsync(h, staged);
        Assert.Equal("0", stored.FacetId);
        Assert.Equal(PlaneOf(0.45, 0.35).A, stored.PlaneAMm!.Value, 1);
    }

    [Fact]
    public void Builder_DrawsAnUnplacedHoldWithAPanelPosition_AsApproximate()
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

        var lost = new Hold
        {
            WallId = wall.Id, WallPanelId = panelId, Generation = 2, X = 0.4, Y = 0.4, Radius = 0.02,
            ShapePoints = ShapePoint.DefaultOctagon(0.02),
        };
        wall.Holds.Add(lost);

        var view = Wall3DViewBuilder.Build(wall, WallGeometryDocument.Parse(Wall3DViewBuilderTests.SolvedJson), null);

        Assert.Equal(0, view.UnplacedHoldCount);
        var drawn = view.Holds.Single(x => x.Id == lost.Id);
        Assert.True(drawn.PlacementApproximate);
        Assert.Equal("0", drawn.FacetId);
        Assert.Equal(500, drawn.PlaneA, 1);
        Assert.Equal(2600, drawn.PlaneB, 1);
        Assert.True(drawn.SizeMeasured);
        Assert.Equal(40, drawn.WidthMm, 1);
        Assert.All(view.Holds.Where(x => x.Id != lost.Id), x => Assert.False(x.PlacementApproximate));
    }

    [Fact]
    public void MarkChanged_KeepsThePosition_ButDropsFootprintAndProtrusion()
    {
        var hold = new Hold
        {
            FacetId = "0", PlaneAMm = 1, PlaneBMm = 2, WidthMm = 3, HeightMm = 4, FootprintMm = "{}", ProtrusionMm = "{}", FingerprintJson = "{}",
        };

        hold.InvalidateGlyphMeasurements();

        Assert.Equal("0", hold.FacetId);
        Assert.Equal(1, hold.PlaneAMm);
        Assert.Null(hold.WidthMm);
        Assert.Null(hold.FootprintMm);
        Assert.Null(hold.ProtrusionMm);
        Assert.Null(hold.FingerprintJson);
    }

    /// <summary>Peer holds on facet "0": a = 1000·x + 100, b = 1250 − 300·y — an exact affine photo → plane map.</summary>
    private static (double A, double B) PlaneOf(double x, double y) => ((1000 * x) + 100, 1250 - (300 * y));

    private static WallService Service(WallTestHarness h, IHoldRefinementQueue? queue) => new(
        h.DbContextFactory, h.CurrentUser, h.HoldDetection, h.ActivityLog, NullLogger<WallService>.Instance, refinementQueue: queue);

    private static HoldEdit Edit(double x, double y, double radius, List<ShapePoint>? shape) =>
        HoldEdit.FromEditorState(x, y, radius, null, HoldCategory.Hand, false, shape, null, null, null, flagBouldersOnMove: false);

    private static async Task<Hold> LoadAsync(WallTestHarness h, Guid holdId)
    {
        await using var db = h.CreateContext();
        return await db.Holds.AsNoTracking().SingleAsync(x => x.Id == holdId);
    }

    /// <summary>A live panel with 10 placed holds (no markers) and an active two-facet model; returns one of them.</summary>
    private static async Task<(Guid PanelId, Hold Edited)> SeedPlacedPanelAsync(WallTestHarness h, int generation = 1)
    {
        await h.SeedWallAsync(holdCount: 0, generation: 1);
        await EnrichmentScenario.AddActiveModelAsync(h, EnrichmentScenario.TwoFacetGeometryJson);
        var panelId = await EnrichmentScenario.AddPanelAsync(h, livePhoto: [1, 2, 3]);
        Hold? edited = null;
        await using var db = h.CreateContext();
        for (var i = 0; i < 10; i++)
        {
            var (x, y) = (0.15 + (0.07 * i), 0.2 + (0.15 * (i % 4)));
            var (a, b) = PlaneOf(x, y);
            var hold = new Hold
            {
                WallId = h.WallId, WallPanelId = panelId, Generation = generation, X = x, Y = y, Radius = 0.02,
                ShapePoints = ShapePoint.DefaultOctagon(0.02), OutlineSource = HoldOutlineSource.AutoContour,
                FacetId = "0", PlaneAMm = a, PlaneBMm = b, WidthMm = 40, HeightMm = 12, AreaMm2 = 400, MetricSource = "multi-view",
            };
            if (i == 3)
            {
                hold.FootprintMm = new HoldFootprint(HoldFootprintSource.MultiView, 3, 40, null, HoldFootprint.KeyOf(hold), [[0, 0], [1, 0], [0, 1]]).ToJson();
                edited = hold;
            }

            db.Holds.Add(hold);
        }

        await db.SaveChangesAsync();
        return (panelId, edited!);
    }
}
