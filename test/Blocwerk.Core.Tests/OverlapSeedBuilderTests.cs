using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The marker seed: a left→right homography in NORMALIZED raw coordinates, fitted from shared marker
/// corners (no model) or composed through a facet plane of the wall model. Synthetic two-facet scene with
/// known plane→image homographies per photo, so every expectation is exact.
/// </summary>
public class OverlapSeedBuilderTests
{
    private const int LW = 4000;
    private const int LH = 3000;
    private const int RW = 3024;
    private const int RH = 4032;

    // Plane (mm) → image (px) per photo and facet. Deliberately projective and different per facet.
    private static readonly PlaneHomography L0 = H(0.9, 0.05, 400, -0.04, -0.85, 2600, 0.00002, 0.00001);
    private static readonly PlaneHomography R0 = H(0.7, -0.08, 200, 0.06, -1.0, 3500, -0.00003, 0.00002);
    private static readonly PlaneHomography L1 = H(0.6, 0.3, 1800, 0.1, -0.9, 2800, 0.00005, -0.00002);
    private static readonly PlaneHomography R1 = H(0.5, 0.2, 900, -0.1, -0.8, 3700, 0.00001, 0.00004);

    [Fact]
    public void SharedMarkers_NoModel_MapsTheirPlaneExactly()
    {
        var left = Photo(LW, LH, (0, L0, 100, 500), (1, L0, 900, 600), (2, L0, 1500, 300));
        var right = Photo(RW, RH, (0, R0, 100, 500), (1, R0, 900, 600), (2, R0, 1500, 300));

        var seed = OverlapSeedBuilder.Build(left, right, null, []);

        Assert.NotNull(seed);
        Assert.Equal(HoldOverlapSeedSource.SharedMarkers, seed!.Source);
        Assert.Equal(3, seed.MarkerCount);
        Assert.True(seed.IsMultiMarker);
        Assert.Equal(12, seed.MeasuredPairs.Count);
        AssertMaps(seed, L0, R0, (700, 900));
        AssertMaps(seed, L0, R0, (1300, 100));
    }

    [Fact]
    public void OneSharedMarker_IsASingleMarkerSeed()
    {
        var left = Photo(LW, LH, (0, L0, 100, 500), (1, L0, 900, 600));
        var right = Photo(RW, RH, (1, R0, 900, 600), (2, R0, 1500, 300));

        var seed = OverlapSeedBuilder.Build(left, right, null, []);

        Assert.Equal(1, seed!.MarkerCount);
        Assert.False(seed.IsMultiMarker);
        AssertMaps(seed, L0, R0, (960, 660));
    }

    [Fact]
    public void NoSharedMarker_NoModel_GivesNoSeed()
    {
        var left = Photo(LW, LH, (0, L0, 100, 500));
        var right = Photo(RW, RH, (2, R0, 1500, 300));

        Assert.Null(OverlapSeedBuilder.Build(left, right, null, []));
    }

    [Fact]
    public void Model_ComposesThroughTheFacet_EvenWithoutASharedMarker()
    {
        var left = Photo(LW, LH, (0, L0, 100, 500), (1, L0, 900, 600));
        var right = Photo(RW, RH, (2, R0, 1500, 300), (3, R0, 300, 100));

        var seed = OverlapSeedBuilder.Build(left, right, Document(), []);

        Assert.Equal(HoldOverlapSeedSource.GeometryFacet, seed!.Source);
        Assert.Equal("0", seed.FacetId);
        Assert.Equal(2, seed.MarkerCount);
        Assert.Empty(seed.MeasuredPairs);
        AssertMaps(seed, L0, R0, (700, 900));
        AssertMaps(seed, L0, R0, (1600, 50));
    }

    [Fact]
    public void Model_PicksTheFacetCarryingMostHolds_AndPredictsEachHoldOnItsOwnFacet()
    {
        var left = Photo(LW, LH, (0, L0, 100, 500), (1, L0, 900, 600), (6, L1, 100, 100), (7, L1, 600, 400));
        var right = Photo(RW, RH, (0, R0, 100, 500), (1, R0, 900, 600), (6, R1, 100, 100), (7, R1, 600, 400));
        var holdsOnFacet1 = new[] { (300.0, 300.0), (500.0, 200.0), (200.0, 450.0) }
            .Select(p => Hold(L1, p, "1"));
        var holdOnFacet0 = Hold(L0, (700, 900), "0");

        var seed = OverlapSeedBuilder.Build(left, right, Document(), [holdOnFacet0, .. holdsOnFacet1]);

        Assert.Equal("1", seed!.FacetId);
        AssertMaps(seed, L1, R1, (400, 250));
        Assert.Equal(4, seed.PriorPairs.Count);

        // The facet-0 hold's prior goes through facet 0, not the chosen facet 1.
        var expected = R0.Apply(700, 900);
        var prior = seed.PriorPairs[0];
        Assert.Equal(expected.X / RW, prior.RightX, 6);
        Assert.Equal(expected.Y / RH, prior.RightY, 6);
    }

    private static void AssertMaps(HoldOverlapSeed seed, PlaneHomography left, PlaneHomography right, (double A, double B) plane)
    {
        var (lx, ly) = left.Apply(plane.A, plane.B);
        var (rx, ry) = right.Apply(plane.A, plane.B);
        var h = PlaneHomography.FromCoefficients(seed.Homography!);
        var (x, y) = h.Apply(lx / LW, ly / LH);
        Assert.Equal(rx / RW, x, 5);
        Assert.Equal(ry / RH, y, 5);
    }

    private static SeedHold Hold(PlaneHomography left, (double A, double B) plane, string facet)
    {
        var (x, y) = left.Apply(plane.A, plane.B);
        return new SeedHold(x / LW, y / LH, facet);
    }

    private static SeedPhoto Photo(int w, int h, params (int Id, PlaneHomography H, double A, double B)[] markers) =>
        new(markers.Select(m => Marker(m.Id, m.H, m.A, m.B, w, h)).ToList(), w, h);

    private static DetectedMarker Marker(int id, PlaneHomography h, double a, double b, int w, int hgt)
    {
        var px = Corners(a, b).Select(c => h.Apply(c[0], c[1])).Select(p => new MarkerPoint(p.X, p.Y)).ToList();
        return new DetectedMarker
        {
            Id = id,
            CornersPx = px,
            CornersNormalized = px.Select(p => new MarkerPoint(p.X / w, p.Y / hgt)).ToList(),
            SidePx = 110,
            EdgeRatio = 1,
        };
    }

    /// <summary>TL, TR, BR, BL of a 125 mm marker whose BL corner is at (a, b); b points up.</summary>
    private static double[][] Corners(double a, double b) => [[a, b + 125], [a + 125, b + 125], [a + 125, b], [a, b]];

    private static WallGeometryDocument Document() => new()
    {
        Version = 1,
        Markers =
        [
            M(0, "0", 100, 500), M(1, "0", 900, 600), M(2, "0", 1500, 300), M(3, "0", 300, 100),
            M(6, "1", 100, 100), M(7, "1", 600, 400),
        ],
    };

    private static WallGeometryMarker M(int id, string facet, double a, double b) =>
        new() { Id = id, Segment = id / 6, Facet = facet, CornersPlaneMm = Corners(a, b) };

    private static PlaneHomography H(double a, double b, double c, double d, double e, double f, double g, double h) =>
        PlaneHomography.FromCoefficients([a, b, c, d, e, f, g, h, 1]);
}
