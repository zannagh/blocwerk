using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The ingest choke points call the enrichment and persist its result in the same save — and an
/// enrichment that throws never fails the ingest.
/// </summary>
public class HoldEnrichmentIngestTests
{
    [Fact]
    public async Task BigUpdateStage_OnGlyphWall_OutlinesAndStoresStagedObservations()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await SetGlyphsAsync(h, 125);
        Detects(h, (0.5, 0.15), (0.5, 0.7));
        var service = new WallBigUpdateService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallBigUpdateService>.Instance, holdEnrichment: Enrichment());

        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());

        await using var db = h.CreateContext();
        var panelId = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);
        var holds = await db.Holds.Where(x => x.WallPanelId == panelId).OrderBy(x => x.Y).ToListAsync();
        Assert.Equal([HoldOutlineSource.AutoContour, HoldOutlineSource.AutoCircle], holds.Select(x => x.OutlineSource));
        Assert.Equal(HoldMetric.LocalMarker, holds[0].MetricSource);
        var observation = await db.WallMarkerObservations.SingleAsync(o => o.WallPanelId == panelId);
        Assert.True(observation.FromStagedPhoto);
        Assert.Equal(1, observation.PanelGeneration);
    }

    [Fact]
    public async Task Redetect_OnNonGlyphWall_OutlinesTheFreshHolds()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var panelId = await AddLivePanelAsync(h);
        Detects(h, (0.5, 0.15));
        var markers = EnrichmentFakes.Markers();
        var service = Panels(h, EnrichmentFakes.Service(EnrichmentFakes.Outlines(), markers));

        Assert.Equal(1, await service.RedetectPanelHoldsAsync(h.WallId, panelId));

        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(x => x.WallPanelId == panelId);
        Assert.Equal(HoldOutlineSource.AutoContour, hold.OutlineSource);
        Assert.NotNull(hold.FingerprintJson);
        Assert.Empty(markers.ReceivedCalls());
    }

    [Fact]
    public async Task Redetect_WhenEnrichmentThrows_StillStoresTheDetectedHolds()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var panelId = await AddLivePanelAsync(h);
        Detects(h, (0.5, 0.15), (0.4, 0.4));
        var broken = Substitute.For<IHoldEnrichmentService>();
        broken.EnrichAsync(default!, default!, default)
            .ReturnsForAnyArgs<Task<HoldEnrichmentSummary>>(_ => throw new InvalidOperationException("boom"));

        Assert.Equal(2, await Panels(h, broken).RedetectPanelHoldsAsync(h.WallId, panelId));

        await using var db = h.CreateContext();
        var holds = await db.Holds.Where(x => x.WallPanelId == panelId).ToListAsync();
        Assert.Equal(2, holds.Count);
        Assert.All(holds, x => Assert.Null(x.OutlineSource));
    }

    [Fact]
    public async Task LegacyUpload_OnGlyphWall_MeasuresButStoresNoObservations()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await SetGlyphsAsync(h, 125);
        Detects(h, (0.5, 0.15));
        var service = new WallService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, h.ActivityLog, NullLogger<WallService>.Instance,
            holdEnrichment: Enrichment());

        await service.UploadPhotoAsync(h.WallId, [1, 2, 3], "image/jpeg");

        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(x => x.WallId == h.WallId);
        Assert.Equal(HoldOutlineSource.AutoContour, hold.OutlineSource);
        Assert.Equal(50, hold.WidthMm!.Value, 3);
        Assert.False(await db.WallMarkerObservations.AnyAsync());
    }

    private static IHoldEnrichmentService Enrichment() => EnrichmentFakes.Service(
        EnrichmentFakes.Outlines(), EnrichmentFakes.Markers(EnrichmentFakes.Square(0, 100, 100, 100)));

    private static WallPanelService Panels(WallTestHarness h, IHoldEnrichmentService enrichment) => new(
        h.DbContextFactory, h.CurrentUser, h.HoldDetection, Substitute.For<IHoldOverlapMatcher>(),
        NullLogger<WallPanelService>.Instance, holdEnrichment: enrichment);

    private static void Detects(WallTestHarness h, params (double X, double Y)[] positions)
    {
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(positions.Select(p => new DetectedHold(p.X, p.Y, 0.02, null, 0.9)).ToList()));
    }

    private static async Task SetGlyphsAsync(WallTestHarness h, double markerSizeMm)
    {
        await using var db = h.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
        wall.GlyphsEnabled = true;
        wall.MarkerSizeMm = markerSizeMm;
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AddLivePanelAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var panel = new WallPanel { WallId = h.WallId, Col = 0, Row = 0, Generation = 0, Photo = [1, 2, 3] };
        db.WallPanels.Add(panel);
        await db.SaveChangesAsync();
        return panel.Id;
    }
}
