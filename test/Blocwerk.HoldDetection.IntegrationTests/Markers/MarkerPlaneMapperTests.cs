using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry;
using Blocwerk.HoldDetection.Markers;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Markers;

/// <summary>
/// Synthetic scene: a 1100 x 800 mm plane with 125 mm markers at known positions, photographed
/// through a known oblique homography (markers ~90-140 px). Detection runs for real, so the only
/// error source is corner localisation on a rendered, interpolated image.
/// </summary>
public class MarkerPlaneMapperTests
{
    private const double MultiToleranceMm = 0.5; // measured ~0.07 mm
    private const double SingleToleranceFraction = 0.005; // of the distance from the marker; measured ~0.07 mm at 300 mm

    private static readonly PlacedMarker[] Placed =
    [
        new(0, 80, 600), new(1, 880, 620), new(3, 90, 80), new(2, 860, 60), new(4, 470, 350),
    ];

    [Fact]
    public void MultiMarker_RecoversPlaneDistancesWithinHalfAMillimetre()
    {
        using var scene = CreateScene();
        var detection = ArucoMarkerDetectionService.Detect(scene.Image, MarkerDetectionOptions.Default);
        Assert.Equal(5, detection.Markers.Count);

        var mapping = MarkerPlaneMapper.Map(detection.Markers, Document(Placed));

        var map = Assert.Single(mapping.Facets);
        Assert.Equal(PlaneMappingMode.MultiMarker, map.Mode);
        Assert.Equal(5, map.MarkerCount);
        Assert.False(map.IsLocalFrame);
        Assert.InRange(map.CornerReprojRmsPx, 0, 1.0);
        Assert.Empty(mapping.OutlierMarkerIds);

        AssertRoundTrip(scene, map, (300, 450), (1000, 750), MultiToleranceMm);
        AssertRoundTrip(scene, map, (20, 20), (1080, 780), MultiToleranceMm);
        AssertScale(scene, map, 550, 400, 0.02);
    }

    [Fact]
    public void SingleMarker_IsExactNearTheMarkerAndDegradesGracefully()
    {
        using var scene = CreateScene();
        var detection = ArucoMarkerDetectionService.Detect(scene.Image, MarkerDetectionOptions.Default);

        // A document that only knows the centre marker: every other id is "unknown".
        var mapping = MarkerPlaneMapper.Map(detection.Markers, Document([Placed[4]]));

        var map = Assert.Single(mapping.Facets);
        Assert.Equal(PlaneMappingMode.SingleMarker, map.Mode);
        Assert.Equal([4], map.MarkerIds);
        Assert.Equal([0, 1, 2, 3], mapping.UnknownMarkerIds.Order());

        // Marker centre (532.5, 412.5); points ~300 mm away.
        foreach (var (a, b) in new[] { (232.5, 412.5), (832.5, 412.5), (532.5, 712.5), (532.5, 112.5) })
        {
            var tolerance = Math.Max(0.5, SingleToleranceFraction * 300);
            AssertRoundTrip(scene, map, (532.5, 412.5), (a, b), tolerance);
        }

        AssertScale(scene, map, 532, 412, 0.02);
    }

    [Fact]
    public void RansacDropsAMarkerWhosePlanePositionIsWrong()
    {
        using var scene = CreateScene();
        var detection = ArucoMarkerDetectionService.Detect(scene.Image, MarkerDetectionOptions.Default);
        var wrong = Placed.Select(p => p.Id == 1 ? p with { A = p.A - 200 } : p).ToArray();

        var mapping = MarkerPlaneMapper.Map(detection.Markers, Document(wrong));

        var map = Assert.Single(mapping.Facets);
        Assert.Equal([1], mapping.OutlierMarkerIds);
        Assert.DoesNotContain(1, map.MarkerIds);
        AssertRoundTrip(scene, map, (300, 450), (1000, 750), MultiToleranceMm);
    }

    [Fact]
    public void LocalFallback_GivesEachMarkerItsOwnSquareFrame()
    {
        using var scene = CreateScene();
        var detection = ArucoMarkerDetectionService.Detect(scene.Image, MarkerDetectionOptions.Default);

        var maps = MarkerPlaneMapper.MapLocal(detection.Markers);

        Assert.Equal(5, maps.Count);
        Assert.All(maps, m => Assert.True(m.IsLocalFrame));
        Assert.All(maps, m => Assert.Null(m.FacetId));
        var marker = detection.Markers.Single(m => m.Id == 4);
        var local = maps.Single(m => m.MarkerIds[0] == 4);
        var tl = local.ImageToPlaneMm(marker.CornersPx[0].X, marker.CornersPx[0].Y);
        var br = local.ImageToPlaneMm(marker.CornersPx[2].X, marker.CornersPx[2].Y);
        Assert.Equal(0, tl.A, 1e-6);
        Assert.Equal(125, tl.B, 1e-6);
        Assert.Equal(125, br.A, 1e-6);
        Assert.Equal(0, br.B, 1e-6);
        AssertScale(scene, local, marker.CenterPx.X, marker.CenterPx.Y, 0.03);
    }

    [Fact]
    public void Placement_ReportsTheFacetAndItsVisibleArea()
    {
        using var scene = CreateScene();
        var detection = ArucoMarkerDetectionService.Detect(scene.Image, MarkerDetectionOptions.Default);
        var extent = new PlaneRectMm(0, 1100, 0, 800);

        var placements = MarkerPhotoPlacement.Place(detection, Document(Placed, extent));

        var p = Assert.Single(placements);
        Assert.Equal(0, p.SegmentIndex);
        Assert.Equal("0", p.FacetId);
        Assert.Equal(new PlaneRectMm(80, 1005, 60, 745), p.MarkersBoundsMm);
        Assert.Equal(extent, p.VisibleBoundsMm);
        Assert.Equal(1.0, p.ExtentCoverage!.Value, 3);
    }

    private static void AssertRoundTrip(
        SyntheticMarkerScene scene,
        FacetPlaneMap map,
        (double A, double B) p,
        (double A, double B) q,
        double toleranceMm)
    {
        var mp = MapTruth(scene, map, p);
        var mq = MapTruth(scene, map, q);
        Assert.InRange(Dist(mp, p), 0, toleranceMm);
        Assert.InRange(Dist(mq, q), 0, toleranceMm);
        Assert.InRange(Math.Abs(Dist(mp, mq) - Dist(p, q)), 0, toleranceMm);

        var back = map.PlaneMmToImage(p.A, p.B);
        var truth = scene.PlaneToImage.Apply(p.A, p.B);
        Assert.InRange(Math.Sqrt(Sq(back.X - truth.X) + Sq(back.Y - truth.Y)), 0, 2.0);
    }

    /// <summary>A truth plane point → photo px (ground truth) → plane mm (under test).</summary>
    private static (double A, double B) MapTruth(SyntheticMarkerScene scene, FacetPlaneMap map, (double A, double B) p)
    {
        var (x, y) = scene.PlaneToImage.Apply(p.A, p.B);
        return map.ImageToPlaneMm(x, y);
    }

    private static void AssertScale(SyntheticMarkerScene scene, FacetPlaneMap map, double x, double y, double relTolerance)
    {
        var truth = scene.PlaneToImage.Inverse()!.Jacobian(x, y);
        var expected = Math.Sqrt(Math.Abs((truth.Dxx * truth.Dyy) - (truth.Dxy * truth.Dyx)));
        Assert.InRange(map.LocalMmPerPx(x, y), expected * (1 - relTolerance), expected * (1 + relTolerance));
    }

    private static double Dist((double A, double B) p, (double A, double B) q) => Math.Sqrt(Sq(p.A - q.A) + Sq(p.B - q.B));

    private static double Sq(double v) => v * v;

    private static SyntheticMarkerScene CreateScene() =>
        SyntheticMarkerScene.Create(
            1100,
            800,
            Placed,
            [new(160, 90), new(1240, 170), new(1290, 930), new(110, 980)],
            new Size(1400, 1050));

    private static WallGeometryDocument Document(IEnumerable<PlacedMarker> markers, PlaneRectMm? extent = null) => new()
    {
        Version = 1,
        Segments = [new WallGeometrySegment { Index = 0, Facets = [new WallGeometryFacet { Id = "0", ExtentMm = extent }] }],
        Markers = markers
            .Select(m => new WallGeometryMarker
            {
                Id = m.Id,
                Segment = 0,
                Facet = "0",
                CornersPlaneMm = SyntheticMarkerScene.PlaneCorners(m),
            })
            .ToList(),
    };
}
