using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="HoldEnrichmentService"/> against a real provider with deterministic CV fakes (see
/// <see cref="EnrichmentFakes"/>): outlines on every wall, markers only on glyph walls, all-or-nothing.
/// </summary>
public class HoldEnrichmentServiceTests
{
    [Fact]
    public async Task NonGlyphWall_GetsOutlinesAndFingerprints_AndNeverRunsMarkers()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var markers = EnrichmentFakes.Markers(EnrichmentFakes.Square(0, 100, 100, 100));
        var service = EnrichmentFakes.Service(EnrichmentFakes.Outlines(), markers);
        var panelId = await EnrichmentScenario.AddPanelAsync(h);

        var run = await EnrichmentScenario.RunAsync(h, service, _ => { }, panelId, (0.5, 0.15), (0.5, 0.7));

        await markers.DidNotReceiveWithAnyArgs().DetectAsync(default!, default, default);
        Assert.False(run.Summary.MarkerPassRan);
        Assert.Equal(0, run.ObservationCount);

        var contour = run.Holds[0];
        Assert.Equal(HoldOutlineSource.AutoContour, contour.OutlineSource);
        Assert.Equal(4, contour.ShapePoints!.Count);
        Assert.NotNull(HoldFingerprint.FromJson(contour.FingerprintJson));
        Assert.Null(contour.WidthMm);

        var circle = run.Holds[1];
        Assert.Equal(HoldOutlineSource.AutoCircle, circle.OutlineSource);
        Assert.Null(circle.ShapePoints);
        Assert.NotNull(circle.FingerprintJson);
    }

    [Fact]
    public async Task ManualHoldsAndExistingShapes_AreNeverTouched()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var service = EnrichmentFakes.Service(EnrichmentFakes.Outlines(), null);
        await using var db = h.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
        var manual = new Hold { WallId = h.WallId, X = 0.3, Y = 0.3, IsAutoDetected = false };
        var shaped = EnrichmentFakes.AutoHold(h.WallId, 0.4, 0.4);
        shaped.ShapePoints = ShapePoint.DefaultOctagon(0.02);
        db.Holds.AddRange(manual, shaped);

        await service.EnrichAsync(db, new HoldEnrichmentRequest([1], wall, [manual, shaped]));

        Assert.Null(manual.ShapePoints);
        Assert.Null(manual.OutlineSource);
        Assert.Equal(8, shaped.ShapePoints.Count);
        Assert.Null(shaped.FingerprintJson);
    }

    [Fact]
    public async Task GlyphWall_WithoutModel_StoresObservationsAndLocalMarkerSizes()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var panelId = await EnrichmentScenario.AddPanelAsync(h);
        await EnrichmentScenario.AddObservationAsync(h, panelId, fromStaged: true, markerId: 33);
        await EnrichmentScenario.AddObservationAsync(h, panelId, fromStaged: false, markerId: 34);
        var service = EnrichmentFakes.Service(
            EnrichmentFakes.Outlines(), EnrichmentFakes.Markers(EnrichmentFakes.Square(0, 100, 100, 100)));

        var run = await EnrichmentScenario.RunAsync(h, service, EnrichmentScenario.Glyphs(125), panelId, (0.5, 0.15), (0.5, 0.7));

        Assert.True(run.Summary.MarkerPassRan);
        await using var db = h.CreateContext();
        var observations = await db.WallMarkerObservations.Where(o => o.WallPanelId == panelId).ToListAsync();
        Assert.Equal([0, 34], observations.Select(o => o.MarkerId).Order());
        var fresh = observations.Single(o => o.MarkerId == 0);
        Assert.True(fresh.FromStagedPhoto);
        Assert.Equal(1, fresh.PanelGeneration);
        Assert.Equal("[[0.1,0.1],[0.2,0.1],[0.2,0.2],[0.1,0.2]]", fresh.CornersJson);

        // 100 px marker = 125 mm → 1.25 mm/px. Contour 40 × 60 px, circle r = 20 px.
        var contour = run.Holds[0];
        Assert.Equal(50, contour.WidthMm!.Value, 3);
        Assert.Equal(75, contour.HeightMm!.Value, 3);
        Assert.Equal(3750, contour.AreaMm2!.Value, 1);
        Assert.Equal(HoldMetric.LocalMarker, contour.MetricSource);
        Assert.Null(contour.FacetId);
        Assert.Null(contour.PlaneAMm);
        var fingerprint = HoldFingerprint.FromJson(contour.FingerprintJson)!;
        Assert.Equal(75, fingerprint.WidthMm!.Value, 3);
        Assert.Equal(50, fingerprint.HeightMm!.Value, 3);

        Assert.Equal(50, run.Holds[1].WidthMm!.Value, 3);
    }

    [Fact]
    public async Task GlyphWall_WithoutModelOrMarkerSize_StoresObservationsButNoMetrics()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var panelId = await EnrichmentScenario.AddPanelAsync(h);
        var service = EnrichmentFakes.Service(
            EnrichmentFakes.Outlines(), EnrichmentFakes.Markers(EnrichmentFakes.Square(0, 100, 100, 100)));

        var run = await EnrichmentScenario.RunAsync(h, service, EnrichmentScenario.Glyphs(null), panelId, (0.5, 0.15));

        Assert.Equal(1, run.ObservationCount);
        Assert.Null(run.Holds[0].WidthMm);
        Assert.Null(run.Holds[0].MetricSource);
    }

    [Fact]
    public async Task GlyphWall_WithActiveModel_PlacesHoldsOnFacetsAndMeasuresThem()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await EnrichmentScenario.AddActiveModelAsync(h, EnrichmentScenario.TwoFacetGeometryJson);
        var service = EnrichmentFakes.Service(EnrichmentFakes.Outlines(), EnrichmentScenario.TwoFacetMarkers());

        var run = await EnrichmentScenario.RunAsync(
            h, service, EnrichmentScenario.Glyphs(null), null, (0.5, 0.15), (0.5, 0.7), (0.95, 0.95));

        // A: inside both facets' extents → the multi-marker facet wins.
        var a = run.Holds[0];
        Assert.Equal("0", a.FacetId);
        Assert.Equal(HoldMetric.MultiMarker, a.MetricSource);
        Assert.Equal(625, a.PlaneAMm!.Value, 1);
        Assert.Equal(1062.5, a.PlaneBMm!.Value, 1);
        Assert.Equal(50, a.WidthMm!.Value, 1);
        Assert.Equal(75, a.HeightMm!.Value, 1);
        Assert.Equal(3750, a.AreaMm2!.Value, 0);

        // B: only facet "2" (one marker) contains it; circle fallback r = 20 px → 16-gon.
        var b = run.Holds[1];
        Assert.Equal("2", b.FacetId);
        Assert.Equal(HoldMetric.SingleMarker, b.MetricSource);
        Assert.Equal(125, b.PlaneAMm!.Value, 1);
        Assert.Equal(375, b.PlaneBMm!.Value, 1);
        Assert.Equal(50, b.WidthMm!.Value, 1);
        Assert.Equal(8 * 25 * 25 * Math.Sin(2 * Math.PI / 16), b.AreaMm2!.Value, 0);

        // E: outside every extent (+ margin) → nothing metric, but still outlined.
        var e = run.Holds[2];
        Assert.Null(e.FacetId);
        Assert.Null(e.WidthMm);
        Assert.NotNull(e.OutlineSource);
        Assert.Equal(0, run.ObservationCount); // legacy upload: no panel, no observations
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task KillSwitches_TurnTheirPassOff(bool outlinesOn, bool markersOn)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var panelId = await EnrichmentScenario.AddPanelAsync(h);
        var outlines = EnrichmentFakes.Outlines();
        var markers = EnrichmentFakes.Markers(EnrichmentFakes.Square(0, 100, 100, 100));
        var service = EnrichmentFakes.Service(outlines, markers, outlinesOn, markersOn);

        var run = await EnrichmentScenario.RunAsync(h, service, EnrichmentScenario.Glyphs(125), panelId, (0.5, 0.15));

        Assert.Equal(outlinesOn, outlines.ReceivedCalls().Any());
        Assert.Equal(markersOn, markers.ReceivedCalls().Any());
        Assert.Equal(outlinesOn, run.Holds[0].OutlineSource is not null);
        Assert.Equal(markersOn ? 1 : 0, run.ObservationCount);
        Assert.Equal(markersOn, run.Holds[0].WidthMm is not null);
    }

    [Fact]
    public async Task AnyFailure_LeavesHoldsExactlyAsDetected()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var panelId = await EnrichmentScenario.AddPanelAsync(h);
        var markers = Substitute.For<IMarkerDetectionService>();
        markers.DetectAsync(default!, default, default)
            .ReturnsForAnyArgs<Task<MarkerDetectionResult>>(_ => throw new InvalidOperationException("aruco exploded"));
        var service = EnrichmentFakes.Service(EnrichmentFakes.Outlines(), markers);

        var run = await EnrichmentScenario.RunAsync(h, service, EnrichmentScenario.Glyphs(125), panelId, (0.5, 0.15));

        Assert.True(run.Summary.Failed);
        Assert.Null(run.Holds[0].ShapePoints);
        Assert.Null(run.Holds[0].OutlineSource);
        Assert.Null(run.Holds[0].FingerprintJson);
        Assert.Null(run.Holds[0].WidthMm);
        Assert.Equal(0, run.ObservationCount);
    }
}
