using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Place existing holds on the 3D model" on <see cref="HoldPlacementScenario"/>: facet assignment on a photo that
/// sees two facets, what is skipped, and what the 3D view draws afterwards. Revert and access: <see cref="HoldTexturePlacementRevertTests"/>.
/// </summary>
public class HoldTexturePlacementTests
{
    [Fact]
    public async Task MultiFacetPhoto_PlacesEachHoldOnTheFacetThatContainsIt()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var left = await s.AddHoldAsync(0.25, 0.5);
        var right = await s.AddHoldAsync(0.75, 0.2);
        var seam = await s.AddHoldAsync(0.5, 0.5);
        var onC1 = await s.AddHoldAsync(0.5, 0.5, s.PanelC1);
        var before = await s.LoadHoldsAsync();

        var result = await s.Service().PlaceAsync(h.WallId);

        Assert.Equal((3, 0, 1), (result.Placed, result.Skipped, result.Failed));
        var holds = await s.LoadHoldsAsync();
        AssertPlaced(holds[left], "0", 1000, 1500);
        AssertPlaced(holds[right], "1", 1000, 2400);

        // On the fold both facets contain it; facet 1 has more inliers.
        AssertPlaced(holds[seam], "1", 0, 1500);
        Assert.Null(holds[onC1].FacetId);
        Assert.All(holds.Values, x => AssertPhotoUntouched(before[x.Id], x));

        var c0 = result.Panels.Single(p => p.PanelId == s.PanelC0);
        Assert.Equal(new[] { "0", "1" }, c0.Facets.Where(f => f.Accepted).Select(f => f.FacetId).ToArray());
        Assert.True(c0.Facets.All(f => f.Inliers >= 40 && f.Coverage >= 0.2 && f.RmsMm < 0.01));
        var c1 = result.Panels.Single(p => p.PanelId == s.PanelC1);
        Assert.Equal((0, 1), (c1.Placed, c1.Failed));
        Assert.NotNull(c1.Problem);
        s.Queue.Received(1).Enqueue(h.WallId, Arg.Is<IEnumerable<Guid>>(ids => ids.Order().SequenceEqual(new[] { left, right, seam }.Order())));
    }

    [Fact]
    public async Task OwnFacetWins_WhenSeveralFacetsContainTheHold()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var seam = await s.AddHoldAsync(0.5, 0.5, configure: x => Place(x, "0", 1, 1, HoldMetric.TextureRegistration));

        await s.Service().PlaceAsync(h.WallId);

        AssertPlaced((await s.LoadHoldsAsync())[seam], "0", 2000, 1500);
    }

    [Fact]
    public async Task HoldsPlacedByOtherMeans_AreSkipped_AndEarlierTexturePlacementsAreRecomputed()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var byMarkers = await s.AddHoldAsync(0.25, 0.5, configure: x =>
        {
            Place(x, "0", 5, 5, HoldMetric.MultiMarker);
            x.WidthMm = 42;
        });
        var byEdit = await s.AddHoldAsync(0.3, 0.5, configure: x => Place(x, "0", 7, 7, HoldMetric.HoldFit));
        var @virtual = await s.AddHoldAsync(0.4, 0.5, configure: x => x.IsVirtual = true);
        var earlier = await s.AddHoldAsync(0.25, 0.25, configure: x => Place(x, "1", 9, 9, HoldMetric.TextureRegistration));
        var before = await s.LoadHoldsAsync();

        var result = await s.Service().PlaceAsync(h.WallId);

        Assert.Equal((1, 3), (result.Placed, result.Skipped));
        var holds = await s.LoadHoldsAsync();
        Assert.Equivalent(before[byMarkers], holds[byMarkers]);
        Assert.Equivalent(before[byEdit], holds[byEdit]);
        Assert.Equivalent(before[@virtual], holds[@virtual]);
        AssertPlaced(holds[earlier], "0", 1000, 2250);
    }

    [Fact]
    public async Task Builder_DrawsThePlacedHolds_ThatItCountedAsUnplacedBefore()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        foreach (var (x, y) in new[] { (0.1, 0.1), (0.3, 0.6), (0.6, 0.4), (0.9, 0.9) })
        {
            await s.AddHoldAsync(x, y);
        }

        var doc = WallGeometryDocument.Parse(HoldPlacementScenario.TwoFacetJson);
        var beforeView = Wall3DViewBuilder.Build(await LoadWallAsync(h), doc, null);
        await s.Service().PlaceAsync(h.WallId);
        var view = Wall3DViewBuilder.Build(await LoadWallAsync(h), doc, null);

        Assert.Equal(4, beforeView.UnplacedHoldCount);
        Assert.Equal(0, view.UnplacedHoldCount);
        Assert.Equal(4, view.Holds.Count);
        Assert.All(view.Holds, x => Assert.False(x.PlacementApproximate));
        Assert.All(view.Holds, x => Assert.True(x.SizeMeasured));
        Assert.Equal(new[] { "0", "0", "1", "1" }, view.Holds.Select(x => x.FacetId).Order().ToArray());
    }

    [Fact]
    public async Task Pipeline_PlacesOnlyOnTheActiveModel_AndOnlyTheHoldsNotOnItYet()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        await s.AddHoldAsync(0.25, 0.5);
        var service = s.Service();

        Assert.Null(await service.PlaceFromPipelineAsync(h.WallId, Guid.NewGuid(), h.Owner.Id));
        var first = await service.PlaceFromPipelineAsync(h.WallId, s.ModelId, h.Owner.Id);
        var second = await service.PlaceFromPipelineAsync(h.WallId, s.ModelId, h.Owner.Id);

        Assert.Equal(1, first!.Placed);
        Assert.Null(second);
        s.Queue.DidNotReceiveWithAnyArgs().Enqueue(default, default!);
        await using var db = h.CreateContext();
        Assert.Equal(HoldPlacementTrigger.Capture, (await db.HoldPlacementRuns.SingleAsync()).Trigger);
    }

    [Fact]
    public async Task Pipeline_AddsNewHoldsToAPlacedWall_AndNeverMovesMarkerPlacedOrPlacedOnes()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var byMarkers = await s.AddHoldAsync(0.25, 0.5, configure: x => Place(x, "0", 5, 5, HoldMetric.MultiMarker));
        var byMarkersOnAnOldFacet = await s.AddHoldAsync(0.3, 0.5, configure: x => Place(x, "gone", 6, 6, HoldMetric.MultiMarker));
        var placedBefore = await s.AddHoldAsync(0.75, 0.2, configure: x => Place(x, "1", 9, 9, HoldMetric.TextureRegistration));
        var onAnOldFacet = await s.AddHoldAsync(0.25, 0.25, configure: x => Place(x, "gone", 9, 9, HoldMetric.TextureRegistration));
        var fresh = await s.AddHoldAsync(0.4, 0.5);
        var before = await s.LoadHoldsAsync();

        var result = await s.Service().PlaceFromPipelineAsync(h.WallId, s.ModelId, h.Owner.Id);

        Assert.Equal(2, result!.Placed);
        var holds = await s.LoadHoldsAsync();
        Assert.Equivalent(before[byMarkers], holds[byMarkers]);
        Assert.Equivalent(before[byMarkersOnAnOldFacet], holds[byMarkersOnAnOldFacet]);
        Assert.Equivalent(before[placedBefore], holds[placedBefore]);
        AssertPlaced(holds[onAnOldFacet], "0", 1000, 2250);
        AssertPlaced(holds[fresh], "0", 1600, 1500);
        Assert.All(holds.Values, x => AssertPhotoUntouched(before[x.Id], x));
    }

    internal static void AssertPlaced(Hold hold, string facet, double a, double b)
    {
        Assert.Equal(facet, hold.FacetId);
        Assert.Equal(a, hold.PlaneAMm!.Value, 3);
        Assert.Equal(b, hold.PlaneBMm!.Value, 3);
        Assert.Equal(HoldMetric.TextureRegistration, hold.MetricSource);
        Assert.True(hold.WidthMm > 0 && hold.HeightMm > 0 && hold.AreaMm2 > 0);
    }

    internal static void Place(Hold hold, string facet, double a, double b, string source)
    {
        hold.FacetId = facet;
        hold.PlaneAMm = a;
        hold.PlaneBMm = b;
        hold.MetricSource = source;
    }

    private static void AssertPhotoUntouched(Hold before, Hold after)
    {
        Assert.Equal((before.X, before.Y, before.Radius, before.WallPanelId, before.Generation), (after.X, after.Y, after.Radius, after.WallPanelId, after.Generation));
        Assert.Equivalent(before.ShapePoints, after.ShapePoints);
    }

    private static async Task<Wall> LoadWallAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.Walls.AsNoTracking()
            .Include(w => w.Holds)
            .Include(w => w.Boulders).ThenInclude(b => b.BoulderHolds)
            .SingleAsync(w => w.Id == h.WallId);
    }
}
