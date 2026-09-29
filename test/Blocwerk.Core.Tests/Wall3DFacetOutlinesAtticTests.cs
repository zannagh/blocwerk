// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The Attic's two side triangles, with the solved planes, extents and marker corners of the real model.
/// "closing up space" (5) rests on its plan parent "leftover bit" (2). "closing up end" (7) is attached in the
/// plan to "corner" (6), whose seam only shaves the extent's edge; its hypotenuse really runs along the
/// 45° overhang, which "leftover bit" continues ~570 mm away (<see cref="Wall3DFacetOutlines"/>).
/// </summary>
public class Wall3DFacetOutlinesAtticTests
{
    private const string Markers = """
        [
          { "id": 27, "segment": 7, "facet": "7", "cornersPlaneMm": [[0, 99.94], [99.94, 103.33], [103.33, 3.39], [3.39, 0]] },
          { "id": 28, "segment": 7, "facet": "7", "cornersPlaneMm": [[841.03, 107.74], [941.03, 107.83], [941.12, 7.83], [841.12, 7.74]] },
          { "id": 29, "segment": 7, "facet": "7", "cornersPlaneMm": [[1645.63, 1462.34], [1725.62, 1463.85], [1727.12, 1383.86], [1647.14, 1382.35]] },
          { "id": 30, "segment": 7, "facet": "7", "cornersPlaneMm": [[2095.84, 1926.83], [2195.7, 1921.48], [2190.35, 1821.62], [2090.49, 1826.97]] },
          { "id": 42, "segment": 5, "facet": "5", "cornersPlaneMm": [[1658.48, 100], [1758.48, 100.46], [1758.94, 0.47], [1658.94, 0]] },
          { "id": 43, "segment": 5, "facet": "5", "cornersPlaneMm": [[1960.15, 138.29], [2060.11, 141.04], [2062.86, 41.08], [1962.9, 38.32]] },
          { "id": 44, "segment": 5, "facet": "5", "cornersPlaneMm": [[0, 1969.33], [99.85, 1974.81], [105.33, 1874.96], [5.48, 1869.48]] },
          { "id": 45, "segment": 5, "facet": "5", "cornersPlaneMm": [[932.67, 1428.32], [1012.66, 1429.05], [1013.4, 1349.05], [933.4, 1348.32]] }
        ]
        """;

    private static readonly Dictionary<int, int> Parents = new() { [5] = 2, [7] = 6 };

    private static readonly Dictionary<string, string> Segments = new()
    {
        ["0"] = """{ "index": 0, "name": "main wall", "facets": [ { "id": "0", "origin": [0, 0, 0], "u": [1, 0, 0], "v": [0, -0.722935, 0.690916], "normal": [0, -0.690916, -0.722935], "extentMm": { "aMin": -50, "aMax": 5130.4, "bMin": -50, "bMax": 3313.1 } } ] }""",
        ["2"] = """{ "index": 2, "name": "leftover bit", "facets": [ { "id": "2", "origin": [5053.54, -6.37, 5.34], "u": [0.99998, 0.006392, 0], "v": [0.004622, -0.723117, 0.69071], "normal": [0.004415, -0.690696, -0.723132], "extentMm": { "aMin": -50, "aMax": 694.3, "bMin": -50, "bMax": 2397.3 } } ] }""",
        ["3"] = """{ "index": 3, "name": "kickboard", "facets": [ { "id": "3", "origin": [-10.21, -2.98, -273.69], "u": [0.999997, 0.002303, 0], "v": [0.000051, -0.022101, 0.999756], "normal": [0.002302, -0.999753, -0.022101], "extentMm": { "aMin": -50, "aMax": 5759.4, "bMin": -50, "bMax": 319.4 } } ] }""",
        ["5"] = """{ "index": 5, "name": "closing up space", "facets": [ { "id": "5", "origin": [5721.89, -1632.33, -273.83], "u": [-0.003002, 0.999995, 0], "v": [0.003233, 0.00001, 0.999995], "normal": [0.99999, 0.003002, -0.003233], "extentMm": { "aMin": -50, "aMax": 2112.9, "bMin": -50, "bMax": 2024.8 } } ] }""",
        ["6"] = """{ "index": 6, "name": "corner", "facets": [ { "id": "6", "origin": [5724.02, 447.46, -266.74], "u": [0.999977, 0.006773, 0], "v": [-0.000152, 0.022368, 0.99975], "normal": [0.006772, -0.999727, 0.022369], "extentMm": { "aMin": -50, "aMax": 608.7, "bMin": -50, "bMax": 427.7 } } ] }""",
        ["7"] = """{ "index": 7, "name": "closing up end", "facets": [ { "id": "7", "origin": [6290.2, 443.78, -252.45], "u": [0.027066, -0.999634, 0], "v": [-0.010459, -0.000283, 0.999945], "normal": [-0.999579, -0.027065, -0.010463], "extentMm": { "aMin": -50, "aMax": 2245.7, "bMin": -50, "bMax": 1976.8 } } ] }""",
    };

    [Fact]
    public void ClosingUpEnd_IsCutAlongTheOverhang_WhenItsPlanParentOnlyShavesAnEdge()
    {
        var outline = Facet(Build("0", "2", "3", "5", "6", "7"), "7").Outline;

        Assert.NotNull(outline);

        // Right angle at the floor, room side; the hypotenuse runs up the overhang's plane (its top end is
        // 4 mm from the extent's corner, so merged into it).
        AssertPoints([[120, -50], [2245.7, -50], [2245.7, 1976.8]], outline!, 1);
    }

    [Fact]
    public void ClosingUpEnd_IsCutAlongTheOverhang_WithoutAnyCornerFacet()
    {
        var outline = Facet(Build("0", "2", "3", "5", "7"), "7").Outline;

        Assert.NotNull(outline);
        AssertPoints([[120, -50], [2245.7, -50], [2245.7, 1976.8]], outline!, 1);
    }

    [Fact]
    public void ClosingUpSpace_StaysCutAgainstItsPlanParent_AsATriangle()
    {
        var outline = Facet(Build("0", "2", "3", "5", "6", "7"), "5").Outline;

        Assert.NotNull(outline);

        // Right angle at the top, back; completed past the extent's 50 mm margin at the hypotenuse's ends.
        AssertPoints([[2112.9, -181.9], [2112.9, 2024.8], [-197.3, 2024.8]], outline!, 1);
    }

    [Fact]
    public void ParentSeamAtTheExtentEdge_CutsNothing()
    {
        var view = Build("6", "7");

        var end = Facet(view, "7");
        Assert.Null(Wall3DFacetOutlines.Clip(end, Facet(view, "6"), (1193, 852)));
        Assert.Null(end.Outline);
    }

    [Fact]
    public void OnlyDistantSeams_KeepTheRectangle()
    {
        // Without the overhang facets only the kickboard's seam cuts, ~1 m from the kickboard.
        var view = Build("3", "6", "7");

        var end = Facet(view, "7");
        var kickboard = Facet(view, "3");
        var outline = Wall3DFacetOutlines.Clip(end, kickboard, (1193, 852));
        Assert.NotNull(outline);
        Assert.True(Wall3DFacetOutlines.SeamGapMm(end, kickboard, outline!) > Wall3DFacetOutlines.MaxSeamGapMm);
        Assert.Null(end.Outline);
        Assert.Equal(4, end.Corners.Count);
    }

    private static Wall3DView Build(params string[] ids)
    {
        var json = $$"""
            { "version": 1, "units": "mm", "markerSizeMm": 125.0,
              "segments": [ {{string.Join(",\n", ids.Select(id => Segments[id]))}} ],
              "markers": {{Markers}} }
            """;
        return Wall3DViewBuilder.Build(new Wall { Name = "The Attic" }, WallGeometryDocument.Parse(json), null, hypotenuseParents: Parents);
    }

    private static Wall3DFacet Facet(Wall3DView view, string id) => view.Facets.Single(f => f.Id == id);

    private static void AssertPoints(double[][] expected, IReadOnlyList<double[]> actual, double tolerance)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i][0], actual[i][0], tolerance);
            Assert.Equal(expected[i][1], actual[i][1], tolerance);
        }
    }
}
