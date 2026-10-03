// <copyright file="Wall3DHoldShapeTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Hold outlines in the 3D view: the traced photo outline must land on the facet as its real mm shape,
/// mapped vertex by vertex through the photo's perspective (not a centre + scale approximation), with
/// pocket holes kept, and shape-less holds drawn as an ellipse of their size.
/// </summary>
public class Wall3DHoldShapeTests
{
    private const string Json = """
        {
          "version": 1, "units": "mm", "markerSizeMm": 125.0,
          "segments": [ { "index": 0, "name": "main wall", "facets": [ { "id": "0", "origin": [0, 0, 0],
            "u": [1, 0, 0], "v": [0, -0.7071, 0.7071], "normal": [0, -0.7071, -0.7071],
            "extentMm": { "aMin": 0, "aMax": 3000, "bMin": 0, "bMax": 3000 } } ] } ],
          "markers": [
            { "id": 10, "segment": 0, "facet": "0", "cornersPlaneMm": [[100, 225], [225, 225], [225, 100], [100, 100]] },
            { "id": 11, "segment": 0, "facet": "0", "cornersPlaneMm": [[2600, 525], [2725, 525], [2725, 400], [2600, 400]] },
            { "id": 12, "segment": 0, "facet": "0", "cornersPlaneMm": [[1200, 2825], [1325, 2825], [1325, 2700], [1200, 2700]] }
          ]
        }
        """;

    private const double A0 = 1500;
    private const double B0 = 2200;

    /// <summary>
    /// Plane mm → normalised photo: the facet's top recedes (w grows with b) and x/y scale differently
    /// (a non-square photo), so a plane square appears as a squeezed trapezoid.
    /// </summary>
    private static readonly PlaneHomography Camera =
        PlaneHomography.FromCoefficients([2e-4, 0, 0.1, 0, -1e-4, 0.8, 0, 1e-3, 1]);

    private static readonly Guid PanelId = Guid.NewGuid();

    [Fact]
    public void SquareOnAForeshortenedFacet_MapsBackToAMillimetreSquare_ThroughTheMarkers()
    {
        var wall = NewWall();
        var hold = AddShapedHold(wall, A0, B0, halfMm: 60, holeHalfMm: 20);

        // The photo really is foreshortened: the square's top edge is shorter than its bottom edge.
        var shape = hold.ShapePoints!;
        Assert.True(shape[1].Dx - shape[0].Dx < 0.97 * (shape[2].Dx - shape[3].Dx));

        var view = Wall3DViewBuilder.Build(wall, Doc(), null, PhotoMarkers());
        var mapped = view.Holds.Single().Shape!;

        Assert.Equal(Wall3DShapeSource.Markers, mapped.Source);
        AssertSquare(mapped.Outline, 60);
        AssertSquare(Assert.Single(mapped.Holes), 20);
    }

    [Fact]
    public void WithoutMarkerObservations_ThePhotosPlacedHoldsFitTheMapping()
    {
        var wall = NewWall();
        var hold = AddShapedHold(wall, A0, B0, halfMm: 60, holeHalfMm: 20);
        for (var i = 0; i < 12; i++)
        {
            AddPlacedHold(wall, 200 + (i % 4 * 800), 300 + (i / 4 * 1100));
        }

        var view = Wall3DViewBuilder.Build(wall, Doc(), null);
        var mapped = view.Holds.Single(h => h.Id == hold.Id).Shape!;

        Assert.Equal(Wall3DShapeSource.HoldFit, mapped.Source);
        AssertSquare(mapped.Outline, 60);
        AssertSquare(Assert.Single(mapped.Holes), 20);
    }

    [Fact]
    public void HoldWithoutAShape_IsACircleOfItsSize()
    {
        var wall = NewWall();
        AddPlacedHold(wall, A0, B0);

        var shape = Wall3DViewBuilder.Build(wall, Doc(), null, PhotoMarkers()).Holds.Single().Shape!;

        Assert.Equal(Wall3DShapeSource.Circle, shape.Source);
        Assert.Equal(HoldShapeProjector.CircleVertices, shape.Outline.Count);
        Assert.All(shape.Outline, p => Assert.Equal(Wall3DViewBuilder.DefaultHandSizeMm / 2, Math.Sqrt((p[0] * p[0]) + (p[1] * p[1])), 0.2));
        Assert.Empty(shape.Holes);
    }

    [Fact]
    public void NoMappingAtAll_StretchesTheOutlineToTheMeasuredSize()
    {
        var wall = NewWall();
        var hold = AddShapedHold(wall, A0, B0, halfMm: 60, holeHalfMm: null);
        hold.WidthMm = 120;
        hold.HeightMm = 120;

        var shape = Wall3DViewBuilder.Build(wall, Doc(), null).Holds.Single().Shape!;

        Assert.Equal(Wall3DShapeSource.Approximate, shape.Source);
        Assert.Equal(120, shape.Outline.Max(p => p[0]) - shape.Outline.Min(p => p[0]), 0.2);
        Assert.Equal(120, shape.Outline.Max(p => p[1]) - shape.Outline.Min(p => p[1]), 0.2);
    }

    [Fact]
    public void DenseOutlines_AreCappedButKeepTheirExtent()
    {
        var ring = Enumerable.Range(0, 400)
            .Select(i => 2 * Math.PI * i / 400)
            .Select(t => (100 * Math.Cos(t), 50 * Math.Sin(t)))
            .ToList();

        var capped = PolygonSimplifier.Cap(ring, HoldShapeProjector.MaxOutlineVertices);

        Assert.Equal(HoldShapeProjector.MaxOutlineVertices, capped.Count);
        Assert.InRange(capped.Max(p => p.A) - capped.Min(p => p.A), 195, 200.01);
        Assert.InRange(capped.Max(p => p.B) - capped.Min(p => p.B), 95, 100.01);
    }

    private static WallGeometryDocument Doc() => WallGeometryDocument.Parse(Json);

    private static Wall NewWall() => new() { Name = "Shapes", CurrentGeneration = 2 };

    private static (double X, double Y) Photo(double a, double b) => Camera.Apply(a, b);

    private static Hold AddPlacedHold(Wall wall, double a, double b)
    {
        var (x, y) = Photo(a, b);
        var hold = new Hold
        {
            WallId = wall.Id, WallPanelId = PanelId, Generation = 2, X = x, Y = y,
            FacetId = "0", PlaneAMm = a, PlaneBMm = b,
        };
        wall.Holds.Add(hold);
        return hold;
    }

    /// <summary>A hold whose traced outline is the photo image of a plane square (± a pocket square).</summary>
    private static Hold AddShapedHold(Wall wall, double a, double b, double halfMm, double? holeHalfMm)
    {
        var hold = AddPlacedHold(wall, a, b);
        hold.ShapePoints = SquareInPhoto(hold, a, b, halfMm);
        if (holeHalfMm is { } h)
        {
            hold.ShapeHoles = [SquareInPhoto(hold, a, b, h)];
        }

        return hold;
    }

    /// <summary>Corners TL, TR, BR, BL of a plane square, as ShapePoints relative to the hold's photo centre.</summary>
    private static List<ShapePoint> SquareInPhoto(Hold hold, double a, double b, double half) =>
        new (double A, double B)[] { (a - half, b + half), (a + half, b + half), (a + half, b - half), (a - half, b - half) }
            .Select(p => Photo(p.A, p.B))
            .Select(p => new ShapePoint { Dx = p.X - hold.X, Dy = p.Y - hold.Y })
            .ToList();

    /// <summary>The photo's marker observations as stored: normalised corners and the side in (4000×3000) pixels.</summary>
    private static Dictionary<Wall3DPhotoKey, Wall3DPhotoMarkers> PhotoMarkers()
    {
        var doc = Doc();
        var rows = new[] { 10, 11, 12 }.Select(id =>
        {
            var corners = doc.FindMarker(id)!.CornersPlaneMm.Select(c => Photo(c[0], c[1])).ToArray();
            var side = Enumerable.Range(0, 4).Average(i => Math.Sqrt(
                Math.Pow((corners[(i + 1) % 4].X - corners[i].X) * 4000, 2) + Math.Pow((corners[(i + 1) % 4].Y - corners[i].Y) * 3000, 2)));
            return new WallMarkerObservation
            {
                WallPanelId = PanelId, PanelGeneration = 2, MarkerId = id, SidePx = side,
                CornersJson = JsonSerializer.Serialize(corners.Select(c => new[] { c.X, c.Y })),
            };
        }).ToList();
        return new() { [new Wall3DPhotoKey(PanelId, 2)] = Wall3DPhotoMarkerLoader.ToPhoto(rows) };
    }

    private static void AssertSquare(IReadOnlyList<double[]> ring, double half)
    {
        Assert.Equal(4, ring.Count);
        (double, double)[] expected = [(-half, half), (half, half), (half, -half), (-half, -half)];
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(expected[i].Item1, ring[i][0], 0.5);
            Assert.Equal(expected[i].Item2, ring[i][1], 0.5);
        }
    }
}
