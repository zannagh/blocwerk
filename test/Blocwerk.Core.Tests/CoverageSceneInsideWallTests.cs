// <copyright file="CoverageSceneInsideWallTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Capture.Coverage;
using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Which parts of a facet's region lie inside the wall: only a facet whose plane meets the region hides part of it
/// (an adjacent side panel's rectangle poking behind the wall it meets), never a distant or parallel panel.
/// </summary>
public class CoverageSceneInsideWallTests
{
    private static readonly object MainWall = Facet("main", [0, 0, 0], [1, 0, 0], [0, -1, 0], 0, 5130);

    [Fact]
    public void AFarSidePanel_DoesNotHideTheMainWall()
    {
        var scene = Scene(MainWall, Facet("far", [5721, 0, 0], [0, -1, 0], [1, 0, 0], -3000, 3000));

        Assert.All(MainWallPoints(), p => Assert.False(scene.InsideWall("main", p), $"({p[0]}, {p[2]}) hidden"));
    }

    [Fact]
    public void AParallelPanel_DoesNotHideTheMainWall()
    {
        var scene = Scene(MainWall, Facet("front", [0, -2000, 0], [1, 0, 0], [0, -1, 0], 0, 5130));

        Assert.All(MainWallPoints(), p => Assert.False(scene.InsideWall("main", p), $"({p[0]}, {p[2]}) hidden"));
    }

    [Fact]
    public void AnAdjacentSidePanel_IsHiddenWhereItsRegionReachesBehindTheWall()
    {
        var scene = Scene(
            MainWall,
            Facet("side", [5000, 0, 0], [0, -1, 0], [1, 0, 0], -1000, 3000),
            Facet("far", [8000, 0, 0], [0, -1, 0], [1, 0, 0], -3000, 3000));

        Assert.True(scene.InsideWall("side", [5000, 500, 1500]));
        Assert.False(scene.InsideWall("side", [5000, -1500, 1500]));
    }

    private static IEnumerable<double[]> MainWallPoints()
    {
        for (var x = 300.0; x < 5000; x += 400)
        {
            for (var z = 300.0; z < 3500; z += 400)
            {
                yield return [x, 0, z];
            }
        }
    }

    private static CoverageScene Scene(params object[] facets)
    {
        var doc = WallGeometryDocument.Parse(JsonSerializer.Serialize(new
        {
            version = 1,
            units = "mm",
            world = new { up = new[] { 0.0, 0, 1 } },
            segments = facets.Select((f, i) => new { index = i, name = $"S{i}", measuredAngleDeg = 0.0, facets = new[] { f } }),
            markers = Array.Empty<object>(),
        }));
        return new CoverageScene(CaptureCoverageAnalyzer.Facets(doc, new Dictionary<string, PlaneRectMm>()), []);
    }

    private static object Facet(string id, double[] origin, double[] u, double[] normal, double aMin, double aMax) => new
    {
        id,
        origin,
        u,
        v = new[] { 0.0, 0, 1 },
        normal,
        yawDeg = 0.0,
        extentMm = new { aMin, aMax, bMin = 0, bMax = 3500 },
    };
}
