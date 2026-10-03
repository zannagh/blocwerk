// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A plan triangle's facet is cut where its solved plane meets its parent's
/// (<see cref="Wall3DFacetOutlines"/>) on the side of its plan right angle, else behind the parent; anything unreliable
/// keeps the rectangle.
/// </summary>
public class Wall3DFacetOutlinesTests
{
    private const double S = 0.7071067811865476;

    /// <summary>
    /// Parent "2": a 45° overhang through the origin (plane y = −z). Triangle "5": a vertical side wall at
    /// x = 1000 whose (a, b) maps to (1000, −a, b), so the seam is the line a = b; its marker sits above it.
    /// Rectangle "7": same side wall, but its segment is no plan triangle.
    /// </summary>
    internal const string Json = """
        {
          "version": 1, "units": "mm", "markerSizeMm": 125.0,
          "segments": [
            { "index": 2, "name": "leftover bit",
              "facets": [ { "id": "2", "origin": [0, 0, 0], "u": [1, 0, 0], "v": [0, -0.7071067811865476, 0.7071067811865476],
                "normal": [0, -0.7071067811865476, -0.7071067811865476],
                "extentMm": { "aMin": 0, "aMax": 1000, "bMin": 0, "bMax": 1400 } } ] },
            { "index": 5, "name": "closing up space",
              "facets": [ { "id": "5", "origin": [1000, 0, 0], "u": [0, -1, 0], "v": [0, 0, 1],
                "extentMm": { "aMin": 0, "aMax": 1000, "bMin": 0, "bMax": 1000 } } ] },
            { "index": 7, "name": "plain",
              "facets": [ { "id": "7", "origin": [1000, 0, 0], "u": [0, -1, 0], "v": [0, 0, 1],
                "extentMm": { "aMin": 0, "aMax": 1000, "bMin": 0, "bMax": 1000 } } ] }
          ],
          "markers": [
            { "id": 42, "segment": 5, "facet": "5", "cornersPlaneMm": [[100, 825], [225, 825], [225, 700], [100, 700]] },
            { "id": 43, "segment": 7, "facet": "7", "cornersPlaneMm": [[100, 825], [225, 825], [225, 700], [100, 700]] }
          ]
        }
        """;

    [Fact]
    public void Triangle_IsCutAtTheSeamWithItsParent_OnItsMarkersSide()
    {
        var triangles = new Dictionary<int, PlanTriangle> { [5] = new(2, TriangleCorner.TopLeft) };
        var view = Wall3DViewBuilder.Build(new Wall { Name = "Attic" }, WallGeometryDocument.Parse(Json), null, planTriangles: triangles);

        var triangle = view.Facets.Single(f => f.Id == "5");
        Assert.NotNull(triangle.Outline);
        AssertPoints([[0, 0], [1000, 1000], [0, 1000]], triangle.Outline!);

        // The world corners follow the outline: (1000, −a, b).
        Assert.Equal(3, triangle.Corners.Count);
        Assert.Equal(new[] { 1000.0, -1000.0, 1000.0 }, triangle.Corners[1]);
        Assert.Equal(new PlaneRectMm(0, 1000, 0, 1000), triangle.Extent);
    }

    [Fact]
    public void NonTriangleSegment_KeepsItsRectangle()
    {
        var triangles = new Dictionary<int, PlanTriangle> { [5] = new(2, TriangleCorner.TopLeft) };
        var view = Wall3DViewBuilder.Build(new Wall { Name = "Attic" }, WallGeometryDocument.Parse(Json), null, planTriangles: triangles);

        var plain = view.Facets.Single(f => f.Id == "7");
        Assert.Null(plain.Outline);
        Assert.Equal(4, plain.Corners.Count);
        Assert.Null(view.Facets.Single(f => f.Id == "2").Outline);
    }

    [Fact]
    public void ParallelPlanes_FallBackToTheRectangle()
    {
        var parent = Facet("2", [0, 0, 0], [1, 0, 0], [0, 0, 1], [0, -1, 0]);
        var child = Facet("5", [0, -300, 0], [1, 0, 0], [0, 0, 1], [0, -1, 0]);

        Assert.Null(Wall3DFacetOutlines.Clip(child, parent, (500, 500)));
    }

    [Fact]
    public void WithoutAFacingNormal_CentroidOnTheSeam_FallsBackToTheRectangle()
    {
        var parent = Facet("2", [0, 0, 0], [1, 0, 0], [0, -S, S], [0, 0, 0]);
        var child = Facet("5", [1000, 0, 0], [0, -1, 0], [0, 0, 1], [-1, 0, 0]);

        Assert.Null(Wall3DFacetOutlines.Clip(child, parent, (500, 503)));
        Assert.Null(Wall3DFacetOutlines.Clip(child, parent, null));
        Assert.NotNull(Wall3DFacetOutlines.Clip(child, parent, (500, 700)));
    }

    [Fact]
    public void WithoutAFacingNormal_MarkersBelowTheSeam_KeepTheLowerTriangle()
    {
        var parent = Facet("2", [0, 0, 0], [1, 0, 0], [0, -S, S], [0, 0, 0]);
        var child = Facet("5", [1000, 0, 0], [0, -1, 0], [0, 0, 1], [-1, 0, 0]);

        AssertPoints([[0, 0], [1000, 0], [1000, 1000]], Wall3DFacetOutlines.Clip(child, parent, (700, 200))!);
    }

    [Fact]
    public void MarkersInFrontOfTheParent_StillKeepTheSideBehindIt()
    {
        // (700, 200) is (1000, −700, 200): in the room under the overhang, where the centroid rule would keep the lower half.
        var parent = Facet("2", [0, 0, 0], [1, 0, 0], [0, -S, S], [0, -S, -S]);
        var child = Facet("5", [1000, 0, 0], [0, -1, 0], [0, 0, 1], [-1, 0, 0]);

        AssertPoints([[0, 0], [1000, 1000], [0, 1000]], Wall3DFacetOutlines.Clip(child, parent, (700, 200))!);
        AssertPoints([[0, 0], [1000, 1000], [0, 1000]], Wall3DFacetOutlines.Clip(child, parent, null)!);
    }

    [Fact]
    public void StrayFloorMarkers_DoNotFlipTheTriangleIntoTheRoom()
    {
        const string Stray = """
            { "id": 44, "segment": 5, "facet": "5", "cornersPlaneMm": [[700, 125], [825, 125], [825, 0], [700, 0]] },
            { "id": 45, "segment": 5, "facet": "5", "cornersPlaneMm": [[850, 125], [975, 125], [975, 0], [850, 0]] },
            """;
        var json = Json.Replace("\"markers\": [", "\"markers\": [" + Stray, StringComparison.Ordinal);
        var triangles = new Dictionary<int, PlanTriangle> { [5] = new(2, TriangleCorner.BottomLeft) };
        var view = Wall3DViewBuilder.Build(new Wall { Name = "Attic" }, WallGeometryDocument.Parse(json), null, planTriangles: triangles);

        AssertPoints([[0, 0], [1000, 1000], [0, 1000]], view.Facets.Single(f => f.Id == "5").Outline!);
    }

    [Fact]
    public void TrapezoidClip_IsNotForcedIntoATriangle()
    {
        static double Side(double a, double b) => a - (0.2 * b) - 300;
        var outline = Wall3DFacetOutlines.ClipRect(new PlaneRectMm(0, 1000, 0, 1000), Side)!;

        Assert.Same(outline, Wall3DFacetOutlines.AsTriangle(outline, Side));
        Assert.Equal(4, outline.Count);
    }

    [Fact]
    public void PlanTriangles_ListsOnlyTrianglesAttachedByTheirHypotenuse_WithTheirRightAngle()
    {
        PlanSegment[] segments =
        [
            new(2, "leftover bit", SegmentShape.Rectangle, 1000, 1400, TriangleCorner.BottomLeft, 45, 0, null),
            new(5, "closing up space", SegmentShape.Triangle, 767, 767, TriangleCorner.BottomRight, 0, 90,
                new PlanAttachment(2, SegmentEdge.Right, SegmentEdge.Hypotenuse, 0)),
            new(6, "leg-attached", SegmentShape.Triangle, 500, 500, TriangleCorner.BottomLeft, 0, 90,
                new PlanAttachment(2, SegmentEdge.Top, SegmentEdge.Bottom, 0)),
            new(8, "rect", SegmentShape.Rectangle, 500, 500, TriangleCorner.BottomLeft, 0, 90,
                new PlanAttachment(2, SegmentEdge.Left, SegmentEdge.Right, 0)),
        ];
        var plan = new MarkerPlan(1, "DICT_4X4_50", null!, segments, []);

        var triangles = Wall3DFacetOutlines.PlanTriangles(plan);

        Assert.Equal(new Dictionary<int, PlanTriangle> { [5] = new(2, TriangleCorner.BottomRight) }, triangles);
        Assert.Empty(Wall3DFacetOutlines.PlanTriangles(null));
    }

    private static Wall3DFacet Facet(string id, double[] origin, double[] u, double[] v, double[] normal) =>
        new(id, 0, id, origin, u, v, normal, [], new PlaneRectMm(0, 1000, 0, 1000), null);

    private static void AssertPoints(double[][] expected, IReadOnlyList<double[]> actual)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i][0], actual[i][0], 1e-6);
            Assert.Equal(expected[i][1], actual[i][1], 1e-6);
        }
    }
}
