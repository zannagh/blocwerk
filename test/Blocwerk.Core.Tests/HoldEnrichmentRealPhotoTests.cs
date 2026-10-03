using System.Globalization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.HoldDetection.Markers;
using Blocwerk.HoldDetection.Outlines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// End to end with the real OpenCV marker detector and outliner on a real wall crop (markers 0, 5, 12).
/// The geometry model is synthetic but consistent with the photo: every detected corner is placed on
/// facet "0" by a fronto-parallel map scaled so a marker side is 125 mm — so the expected plane position
/// of any pixel is known exactly.
/// </summary>
public class HoldEnrichmentRealPhotoTests
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Glyphs", "img2783-topleft.jpg");

    [Fact]
    public async Task RealCrop_WithActiveModel_FillsFacetPlanePositionAndMillimetres()
    {
        var image = await File.ReadAllBytesAsync(Fixture);
        var detection = await new ArucoMarkerDetectionService().DetectAsync(image, null, CancellationToken.None);
        Assert.Equal([0, 5, 12], detection.Markers.Select(m => m.Id));
        var scale = 125.0 / detection.Markers.Average(m => m.SidePx);
        var height = detection.ImageHeight;

        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await EnrichmentScenario.AddActiveModelAsync(h, GeometryFrom(detection, scale));
        var service = new HoldEnrichmentService(
            new BlocwerkSettings(), NullLogger<HoldEnrichmentService>.Instance,
            new OpenCvHoldOutlineService(), new ArucoMarkerDetectionService());

        var run = await EnrichmentScenario.RunAsync(
            h, service, EnrichmentScenario.Glyphs(null), null, image, (0.5, 0.5), (0.3, 0.7));

        Assert.False(run.Summary.Failed);
        Assert.Equal(3, run.Summary.Markers);
        foreach (var hold in run.Holds)
        {
            Assert.Equal("0", hold.FacetId);
            Assert.Equal(HoldMetric.MultiMarker, hold.MetricSource);
            Assert.Equal(hold.X * detection.ImageWidth * scale, hold.PlaneAMm!.Value, 0.5);
            Assert.Equal((height - (hold.Y * height)) * scale, hold.PlaneBMm!.Value, 0.5);
            Assert.InRange(hold.WidthMm!.Value, 1, 500);
            Assert.InRange(hold.AreaMm2!.Value, 1, 250_000);
            Assert.NotNull(hold.OutlineSource);
            Assert.NotNull(HoldFingerprint.FromJson(hold.FingerprintJson)!.AreaMm2);
        }
    }

    [Fact]
    public void KillSwitches_BindFromConfiguration_DefaultOn()
    {
        var defaults = HoldDetectionSettings.Bind(new ConfigurationBuilder().Build());
        var off = HoldDetectionSettings.Bind(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HoldDetection:Outlines:Enabled"] = "false",
                ["HoldDetection:Markers:Enabled"] = "False",
            })
            .Build());

        Assert.True(defaults.OutlinesEnabled);
        Assert.True(defaults.MarkersEnabled);
        Assert.False(off.OutlinesEnabled);
        Assert.False(off.MarkersEnabled);
    }

    private static string GeometryFrom(MarkerDetectionResult detection, double scale)
    {
        var markers = detection.Markers.Select(m =>
        {
            var corners = string.Join(", ", m.CornersPx.Select(c => string.Create(
                CultureInfo.InvariantCulture, $"[{c.X * scale}, {(detection.ImageHeight - c.Y) * scale}]")));
            return $$"""{ "id": {{m.Id}}, "segment": 0, "facet": "0", "cornersPlaneMm": [{{corners}}] }""";
        });
        return $$"""
            {
              "version": 1, "units": "mm", "markerSizeMm": 125.0,
              "segments": [ { "index": 0, "facets": [ { "id": "0",
                "extentMm": { "aMin": -5000, "aMax": 5000, "bMin": -5000, "bMax": 5000 } } ] } ],
              "markers": [ {{string.Join(", ", markers)}} ]
            }
            """;
    }
}
