// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Corrections;
using Blocwerk.Core.Geometry.Footprints;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="WallGeometryModelTransformer"/>: a similarity applied to a geometry document, a texture's bounds and a splat's
/// frame keeps everything consistent (a round trip restores the original, a camera still sees the same pixel, a splat
/// point lands where the moved world puts it), plus dropping a facet and re-deriving the angles.
/// </summary>
public class WallGeometryModelTransformerTests
{
    private static readonly GeometrySimilarity Tilted = Compose(
        1.1, GeometrySimilarity.RotationBetween([0, 0, 1], [0, Math.Sin(0.05), Math.Cos(0.05)], [0, 0, 0]), [120, -40, 15]);

    // Splat (COLMAP) units → world mm: 500 mm per unit, a quarter turn about z, an offset.
    private static readonly double[] World = [0, -500, 0, 1000, 500, 0, 0, -200, 0, 0, 500, 300, 0, 0, 0, 1];

    private static readonly double[] RefinedWorld = [0, -501, 0, 1002, 501, 0, 0, -198, 0, 0, 501, 301, 0, 0, 0, 1];

    private static readonly double[] Centre = [1500, 0, 1250];

    [Fact]
    public void Document_RoundTrip_RestoresFacetsCamerasAndExtents()
    {
        var original = MarkerlessFixture.FeatureDoc(anchored: false);

        var back = WallGeometryModelTransformer.TransformDocument(WallGeometryModelTransformer.TransformDocument(original, Tilted), Inverse(Tilted));

        var a = WallGeometryDocument.Parse(original);
        var b = WallGeometryDocument.Parse(back);
        foreach (var (fa, fb) in a.Segments.SelectMany(s => s.Facets).Zip(b.Segments.SelectMany(s => s.Facets)))
        {
            AssertClose(fa.Origin!, fb.Origin!, 0.05);
            AssertClose(fa.U!, fb.U!, 1e-5);
            AssertClose(fa.Normal!, fb.Normal!, 1e-5);
            Assert.Equal(fa.ExtentMm!.Value.AMax, fb.ExtentMm!.Value.AMax, 0);
        }

        var ca = SolvedCamera.ParseAll(original);
        var cb = SolvedCamera.ParseAll(back);
        AssertClose(ca[1].Centre, cb[1].Centre, 0.05);
    }

    [Fact]
    public void Document_ScalesExtentsAndPlaneCoordinates_AndCamerasStillSeeTheSamePixel()
    {
        var original = MarkerlessFixture.FeatureDoc(anchored: false);

        var moved = WallGeometryModelTransformer.TransformDocument(original, Tilted);

        var before = WallGeometryDocument.Parse(original).FindFacet("0")!.Value.Facet;
        var after = WallGeometryDocument.Parse(moved).FindFacet("0")!.Value.Facet;
        Assert.Equal(before.ExtentMm!.Value.AMax * 1.1, after.ExtentMm!.Value.AMax, 0);
        AssertClose(Tilted.Apply(before.Origin!), after.Origin!, 0.01);

        // A wall point seen by a camera is seen at the same pixel after the move.
        double[] point = [1500, 0, 1200];
        var oldPixel = SolvedCamera.ParseAll(original)[0].Project(point)!.Value;
        var newPixel = SolvedCamera.ParseAll(moved)[0].Project(Tilted.Apply(point))!.Value;
        Assert.Equal(oldPixel.X, newPixel.X, 1);
        Assert.Equal(oldPixel.Y, newPixel.Y, 1);

        // "up" turns with the world.
        AssertClose(Tilted.Rotate([0, 0, 1]), WallGeometryDocument.Parse(moved).World!.Up!, 1e-5);
    }

    [Fact]
    public void TextureBounds_ScaleWithTheWorld()
    {
        var bounds = WallGeometryModelTransformer.TransformTexture(new TextureBounds(-50, 3000, 10, 2500), Tilted);

        AssertClose([-55, 3300, 11, 2750], [bounds.AMin, bounds.AMax, bounds.BMin, bounds.BMax], 1e-9);
    }

    [Fact]
    public void SplatFrame_ToWorldAndViewerMatricesFollowTheSimilarity()
    {
        var frame = SplatFrameJson();

        var moved = JsonNode.Parse(WallGeometryModelTransformer.TransformSplatFrame(frame, Tilted))!;

        double[] splatPoint = [0.3, -1.2, 2.5];
        var oldWorld = Apply(GeometryJson.Matrix4(JsonNode.Parse(frame)!["toWorldMm"])!, splatPoint);
        var newWorld = Apply(GeometryJson.Matrix4(moved["toWorldMm"])!, splatPoint);
        AssertClose(Tilted.Apply(oldWorld), newWorld, 1e-3);
        var refined = Apply(GeometryJson.Matrix4(moved["refinement"]!["toWorldMm"])!, splatPoint);
        AssertClose(Tilted.Apply(Apply(RefinedWorld, splatPoint)), refined, 1e-3);

        // The viewer: metres around the MOVED reference centre, same axes.
        var viewer = Apply(GeometryJson.Matrix4(moved["toViewer"])!, splatPoint);
        var expected = Viewer(newWorld, Tilted.Apply(Centre));
        AssertClose(expected, viewer, 1e-6);
        var matrix = GeometryJson.Numbers(moved["matrix"])!;
        var toViewer = GeometryJson.Matrix4(moved["toViewer"])!;
        Assert.Equal(toViewer[1], matrix[4], 9);
        Assert.Equal(toViewer[3], matrix[12], 9);
        Assert.Equal(500 * 1.1, moved["scaleMmPerUnit"]!.GetValue<double>(), 6);
    }

    [Fact]
    public void DropFacet_RemovesItAndItsEmptySegment()
    {
        var dropped = WallGeometryModelTransformer.DropFacet(MarkerlessFixture.FeatureDoc(anchored: false), "1")!;

        var doc = WallGeometryDocument.Parse(dropped);
        Assert.Equal(["0"], doc.Segments.SelectMany(s => s.Facets).Select(f => f.Id));
        Assert.Single(doc.Segments);
        Assert.Null(WallGeometryModelTransformer.DropFacet(dropped, "7"));
    }

    [Fact]
    public void MarkerCorners_ScaleInThePlane_AndMoveInTheWorld()
    {
        var doc = MarkerlessFixture.MarkerDoc();

        var moved = WallGeometryDocument.Parse(WallGeometryModelTransformer.TransformDocument(doc, GeometrySimilarity.Scaling(2)));

        Assert.Equal(new double[] { 400, 2250 }, moved.FindMarker(0)!.CornersPlaneMm[0]);
    }

    internal static GeometrySimilarity Compose(double scale, GeometrySimilarity rotation, double[] translation) =>
        new(scale, rotation.Rotation, translation);

    internal static GeometrySimilarity Inverse(GeometrySimilarity t)
    {
        var q = t.Rotation;
        double[] qt = [q[0], q[3], q[6], q[1], q[4], q[7], q[2], q[5], q[8]];
        var back = GeometrySimilarity.Multiply(qt, t.Translation);
        return new GeometrySimilarity(1 / t.Scale, qt, [-back[0] / t.Scale, -back[1] / t.Scale, -back[2] / t.Scale]);
    }

    internal static void AssertClose(double[] expected, double[] actual, double tolerance)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, $"[{i}] expected {expected[i]} but got {actual[i]}");
        }
    }

    /// <summary>World mm → viewer metres (x right, y up, z out of the wall), around <paramref name="centre"/>.</summary>
    private static double[] Viewer(double[] world, double[] centre) =>
        [(world[0] - centre[0]) / 1000, (world[2] - centre[2]) / 1000, -(world[1] - centre[1]) / 1000];

    private static string SplatFrameJson()
    {
        // toViewer = V · toWorld with V = [W | −W·c].
        double[] v = [0.001, 0, 0, -Centre[0] / 1000, 0, 0, 0.001, -Centre[2] / 1000, 0, -0.001, 0, Centre[1] / 1000, 0, 0, 0, 1];
        var toViewer = GeometryJson.Multiply4(v, World);
        return new JsonObject
        {
            ["aligned"] = true,
            ["toWorldMm"] = GeometryJson.Rows(World, 9),
            ["toViewer"] = GeometryJson.Rows(toViewer, 9),
            ["matrix"] = GeometryJson.Array(Enumerable.Range(0, 16).Select(i => toViewer[((i % 4) * 4) + (i / 4)]).ToArray(), 9),
            ["scaleMmPerUnit"] = 500.0,
            ["crop"] = new JsonArray(new JsonArray(-2.0, -1.5, -1.0), new JsonArray(2.0, 1.5, 1.0)),
            ["refinement"] = new JsonObject { ["applied"] = true, ["toWorldMm"] = GeometryJson.Rows(RefinedWorld, 9) },
        }.ToJsonString();
    }

    private static double[] Apply(double[] m, double[] p) =>
    [
        (m[0] * p[0]) + (m[1] * p[1]) + (m[2] * p[2]) + m[3],
        (m[4] * p[0]) + (m[5] * p[1]) + (m[6] * p[2]) + m[7],
        (m[8] * p[0]) + (m[9] * p[1]) + (m[10] * p[2]) + m[11],
    ];
}
