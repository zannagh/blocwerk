// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Tests.RealData;

/// <summary>
/// Properties of The Attic's real active model (facets 0 to 7a, 49 plan markers) that were broken on real data only:
/// facets whose extents overlapped (a stray marker stretching one over its coplanar neighbour), duplicated facets after a
/// plan change, and coplanar facets that must meet at the midline between their marker clusters.
/// </summary>
public sealed class AtticFacetsRealDataTests
{
    /// <summary>Two planes count as "the same wall" below this angle, degrees.</summary>
    private const double CoplanarDeg = 10;

    /// <summary>... and within this distance of each other's plane, mm.</summary>
    private const double MaxPlaneOffsetMm = 200;

    [Fact]
    public void TheModelHasEightFacets_OnePerPlanSegment_WithUniqueIds()
    {
        var facets = AtticRealData.Facets();
        using var plan = AtticRealData.Plan();

        Assert.Equal(8, facets.Count);
        Assert.Equal(facets.Count, facets.Select(f => f.Facet.Id).Distinct().Count());
        Assert.Equal(plan.RootElement.GetProperty("segments").GetArrayLength(), AtticRealData.Model.Segments.Count);
        Assert.All(AtticRealData.Model.Segments, s => Assert.Single(s.Facets));
    }

    [Fact]
    public void NoFacetIsADuplicate_NoMarkerIsInTwoFacets_AndEveryPlanMarkerIsPlacedOnce()
    {
        var doc = AtticRealData.Model;
        using var plan = AtticRealData.Plan();
        var planned = plan.RootElement.GetProperty("markers").EnumerateArray().Select(m => m.GetProperty("id").GetInt32()).ToList();

        Assert.Equal(planned.Order(), doc.Markers.Select(m => m.Id).Order());
        Assert.Equal(doc.Markers.Count, doc.Markers.Select(m => m.Id).Distinct().Count());
        Assert.All(AtticRealData.Facets(), f => Assert.NotEmpty(doc.Markers.Where(m => m.Facet == f.Facet.Id)));

        // No two facets share a place and a shape: identical normals, origins within 1 mm and the same extent.
        var facets = AtticRealData.Facets();
        foreach (var (f, g) in Pairs(facets))
        {
            var sameSpot = Math.Abs(Dot(f.Frame.Normal, g.Frame.Normal)) > 0.9999
                && Enumerable.Range(0, 3).All(i => Math.Abs(f.Frame.Origin[i] - g.Frame.Origin[i]) < 1);
            Assert.False(sameSpot, $"facets {f.Facet.Id} and {g.Facet.Id} are duplicates");
        }
    }

    [Fact]
    public void NoTwoNearlyCoplanarFacetsOverlapInPlane()
    {
        var checkedPairs = 0;
        foreach (var (f, g) in Pairs(AtticRealData.Facets()))
        {
            var ab = g.Frame.Corners(g.Extent).Select(c => FacetCloud.Local(f.Frame, c[0], c[1], c[2])).ToList();
            if (!AreCoplanar(f, g, ab))
            {
                continue;
            }

            checkedPairs++;
            var other = new PlaneRectMm(ab.Min(p => p.A), ab.Max(p => p.A), ab.Min(p => p.B), ab.Max(p => p.B));
            var overlap = f.Extent.Intersect(other);

            Assert.True(
                overlap is null || overlap.Value.Width < 1 || overlap.Value.Height < 1,
                $"facets {f.Facet.Id} and {g.Facet.Id} overlap by {overlap?.Width:F0} x {overlap?.Height:F0} mm");
        }

        // Within 10 degrees and 200 mm of one plane there is exactly one pair, main wall / leftover bit (45 deg overhang);
        // every other pair meets at a fold or lies in a different plane.
        Assert.Equal(1, checkedPairs);
    }

    [Fact]
    public void TheMainWallAndTheLeftoverBit_MeetAtTheMidlineBetweenTheirMarkerClusters()
    {
        var facets = AtticRealData.Facets().ToDictionary(f => f.Facet.Id);
        var main = facets["0"];
        var leftover = facets["2"];
        var mainEdge = main.Frame.ToWorld(main.Extent.AMax, 0)[0];
        var leftoverEdge = leftover.Frame.ToWorld(leftover.Extent.AMin, 0)[0];
        var doc = AtticRealData.Model;
        var mainRight = doc.Markers.Where(m => m.Facet == "0").SelectMany(m => m.CornersWorldMm!).Max(c => c[0]);
        var leftoverLeft = doc.Markers.Where(m => m.Facet == "2").SelectMany(m => m.CornersWorldMm!).Min(c => c[0]);

        // Both are cut at one line (to ~1 mm in x): the midline between the two clusters' x ranges (they interleave by ~15 mm).
        Assert.Equal(mainEdge, leftoverEdge, 2.0);
        Assert.Equal((mainRight + leftoverLeft) / 2, mainEdge, 1.0);
        using var json = System.Text.Json.JsonDocument.Parse(AtticRealData.ModelJson);
        var clipped = json.RootElement.GetProperty("quality").GetProperty("checks").GetProperty("overlapClipped");
        Assert.Equal(1, clipped.GetArrayLength());
    }

    [Fact]
    public void MarkerThirtyNine_DeclaredOnTheMainWall_StaysThereAmongItsMarkers()
    {
        var doc = AtticRealData.Model;
        var marker = doc.FindMarker(39)!;
        var main = doc.Markers.Where(m => m.Facet == "0" && m.Id != 39).ToList();
        var leftover = doc.Markers.Where(m => m.Facet == "2").ToList();

        double Nearest(IEnumerable<WallGeometryMarker> ms) => ms.Min(m => Distance(Centre(m), Centre(marker)));

        // It sits at x ~ 4.1 m among the main wall's markers, 1 m from the leftover bit's: the plan puts it on the main wall.
        Assert.Equal("0", marker.Facet);
        Assert.Equal(0, marker.Segment);
        Assert.True(Nearest(main) < Nearest(leftover), $"{Nearest(main):F0} vs {Nearest(leftover):F0} mm");
        Assert.InRange(Nearest(leftover), 800, 2000);
    }

    private static IEnumerable<(T, T)> Pairs<T>(IReadOnlyList<T> items) =>
        items.SelectMany((a, i) => items.Skip(i + 1).Select(b => (a, b)));

    private static bool AreCoplanar(
        (WallGeometryFacet Facet, FacetFrame Frame, PlaneRectMm Extent) f,
        (WallGeometryFacet Facet, FacetFrame Frame, PlaneRectMm Extent) g,
        List<(double A, double B, double H)> gInF) =>
        Math.Acos(Math.Min(1, Math.Abs(Dot(f.Frame.Normal, g.Frame.Normal)))) * 180 / Math.PI < CoplanarDeg
        && gInF.Max(p => Math.Abs(p.H)) < MaxPlaneOffsetMm;

    private static double Dot(double[] a, double[] b) => a.Zip(b, (x, y) => x * y).Sum();

    private static double[] Centre(WallGeometryMarker m) =>
        [.. Enumerable.Range(0, 3).Select(i => m.CornersWorldMm!.Average(c => c[i]))];

    private static double Distance(double[] a, double[] b) => Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum());
}
