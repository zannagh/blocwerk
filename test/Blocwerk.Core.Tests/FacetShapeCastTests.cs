// <copyright file="FacetShapeCastTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Corrections;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Proposals;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.MarkerPlanning;
using static Blocwerk.Core.Tests.HoldProposalFinderTests;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Ray casts onto the model meet a facet only where it really is (the outline the 3D view draws), so a ray through the
/// cut-away half of a triangle panel's rectangle lands on the wall behind it; and one hold's hits on two facets (an
/// arete, a seam) still make one cluster.
/// </summary>
public class FacetShapeCastTests
{
    private const string TwoPanels = """
        {
          "version": 1, "units": "mm", "markerSizeMm": 125.0,
          "segments": [
            { "index": 0, "facets": [ { "id": "0", "origin": [0, 0, 0], "u": [1, 0, 0], "v": [0, 0, 1], "normal": [0, -1, 0],
                "extentMm": { "aMin": 0, "aMax": 3000, "bMin": 0, "bMax": 2000 } } ] },
            { "index": 1, "facets": [ { "id": "1", "origin": [0, -300, 0], "u": [1, 0, 0], "v": [0, 0, 1], "normal": [0, -1, 0],
                "extentMm": { "aMin": 1000, "aMax": 2000, "bMin": 0, "bMax": 1000 } } ] }
          ],
          "markers": []
        }
        """;

    private static readonly FacetFrame Wall = HoldFootprintEstimatorTests.Wall;

    /// <summary>A panel 300 mm in front of the wall; only its lower-left triangle is real.</summary>
    private static readonly FacetFrame Front = FacetFrame.From(new WallGeometryFacet
    {
        Id = "1", Origin = [0, -300, 0], U = [1, 0, 0], V = [0, 0, 1], Normal = [0, -1, 0],
    })!;

    private static readonly PlaneRectMm FrontRect = new(1000, 2000, 0, 1000);

    private static readonly IReadOnlyList<double[]> FrontTriangle = [[1000, 0], [2000, 0], [1000, 1000]];

    [Fact]
    public void TheOutlines_AreTheOnesThe3DViewDraws()
    {
        var doc = WallGeometryDocument.Parse(Wall3DFacetOutlinesTests.Json);
        var triangles = new Dictionary<int, PlanTriangle> { [5] = new(2, TriangleCorner.TopLeft) };

        var outlines = FacetShapes.Outlines(doc, triangles);

        var drawn = Wall3DViewBuilder.Build(new Entities.Wall { Name = "Attic" }, doc, null, planTriangles: triangles).Facets.Single(f => f.Id == "5");
        Assert.Equal(drawn.Outline!.Select(p => (p[0], p[1])), outlines["5"].Select(p => (p[0], p[1])));
        Assert.False(outlines.ContainsKey("7"));
        Assert.True(FacetShapes.Covers(outlines["5"], drawn.Extent, 200, 800, 0));
        Assert.False(FacetShapes.Covers(outlines["5"], drawn.Extent, 800, 200, 20));
        Assert.True(FacetShapes.Covers(outlines["5"], drawn.Extent, 510, 500, 20));
    }

    [Fact]
    public void ADetectionSeenThroughATrianglesEmptyHalf_LandsOnTheWallBehind()
    {
        var camera = Cameras["p1"];
        var target = Wall.ToWorld(1800, 900);
        var detection = DetectIn(camera, target, 60)!;
        var wall = new CastFacet("0", Wall, new PlaneRectMm(0, 3000, 0, 2000), []);

        var throughRect = WallSurfaceCaster.Cast(camera, detection, [wall, new CastFacet("1", Front, FrontRect, [])]);
        var throughShape = WallSurfaceCaster.Cast(camera, detection, [wall, new CastFacet("1", Front, FrontRect, [], FrontTriangle)]);

        Assert.Equal("1", throughRect!.FacetId);
        Assert.Equal("0", throughShape!.FacetId);
        Assert.InRange(RayMath.Length(throughShape.World, target), 0, 1);
    }

    [Fact]
    public void AScaleTapThroughATrianglesEmptyHalf_LandsOnTheWallBehind()
    {
        var doc = WallGeometryDocument.Parse(TwoPanels);
        var camera = Cameras["p1"];
        var target = Wall.ToWorld(1800, 900);
        var px = camera.Project(target)!.Value;

        var onRect = GeometryCorrectionMath.Hit(doc, camera, px.X, px.Y);
        var onShape = GeometryCorrectionMath.Hit(doc, camera, px.X, px.Y, new Dictionary<string, IReadOnlyList<double[]>> { ["1"] = FrontTriangle });

        Assert.Equal("1", onRect!.FacetId);
        Assert.Equal("0", onShape!.FacetId);
        Assert.InRange(RayMath.Length(onShape.World, target), 0, 1);
    }

    [Fact]
    public void OneHoldsHitsOnTwoFacets_MakeOneCluster()
    {
        // Four photos of a hold on a seam: two rays land on facet "0", two on its neighbour "1" (2 + 2, no side has 3).
        var hold = Wall.ToWorld(1500, 1000, 25);
        var cameras = Cameras.Where(c => c.Key != "p5").ToDictionary(c => c.Key, c => c.Value);
        var facets = new[] { new CastFacet("0", Wall, new PlaneRectMm(0, 3000, 0, 2000), []) };
        var hits = cameras.Values
            .Select(c => DetectIn(c, hold, 60))
            .OfType<CaptureDetection>()
            .Select(d => WallSurfaceCaster.Cast(cameras[d.Photo], d, facets))
            .OfType<SurfaceHit>()
            .Select((h, i) => i % 2 == 0 ? h : h with { FacetId = "1" })
            .ToList();

        var cluster = Assert.Single(MultiViewHoldClusterer.Cluster(hits, cameras, 3));

        Assert.Equal(4, cluster.Views);
        Assert.InRange(RayMath.Length(cluster.Point, hold), 0, 5);
    }
}
