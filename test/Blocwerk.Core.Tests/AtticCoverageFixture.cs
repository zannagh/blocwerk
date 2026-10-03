// <copyright file="AtticCoverageFixture.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Capture.Coverage;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A synthetic wall shaped like a real attic capture: a main wall, a side wall and a sloped ceiling, block and
/// flat-sided volumes on them and a few hundred solved cameras. For the analyzer's run-time bound.
/// </summary>
internal static class AtticCoverageFixture
{
    /// <summary>The analyzer's inputs.</summary>
    public static CoverageInputs Inputs(int cameraCount)
    {
        var doc = WallGeometryDocument.Parse(JsonSerializer.Serialize(new
        {
            version = 1,
            units = "mm",
            world = new { up = new[] { 0.0, 0, 1 } },
            segments = new[]
            {
                Segment(0, "Main wall", "0", [0, 0, 0], [1, 0, 0], [0, 0, 1], [0, -1, 0], 5000, 3500),
                Segment(1, "Side wall", "1", [5000, 0, 0], [0, -1, 0], [0, 0, 1], [-1, 0, 0], 3000, 3500),
                Segment(2, "Ceiling", "2", [0, 0, 3500], [1, 0, 0], [0, -0.97, 0.24], [0, -0.24, -0.97], 5000, 2500),
            },
            markers = Array.Empty<object>(),
        }));
        return new CoverageInputs(
            Guid.NewGuid(), Guid.NewGuid(), doc, Cameras(cameraCount), [], Volumes(), new Dictionary<string, PlaneRectMm>(),
            new CoverageVideoInput(false, 0, null));
    }

    private static object Segment(int index, string name, string id, double[] o, double[] u, double[] v, double[] n, double aMax, double bMax) => new
    {
        index,
        name,
        measuredAngleDeg = 0.0,
        facets = new[]
        {
            new { id, origin = o, u, v, normal = n, yawDeg = 0.0, extentMm = new { aMin = 0, aMax, bMin = 0, bMax } },
        },
    };

    private static List<CoverageCamera> Cameras(int count)
    {
        var rng = new Random(42);
        var result = new List<CoverageCamera>();
        for (var i = 0; i < count; i++)
        {
            double[] centre = [-500 + (rng.NextDouble() * 5500), -1500 - (rng.NextDouble() * 3000), 600 + (rng.NextDouble() * 1800)];
            var onSide = rng.NextDouble() < 0.25;
            double[] target = onSide
                ? [5000, -rng.NextDouble() * 3000, 300 + (rng.NextDouble() * 3000)]
                : [rng.NextDouble() * 5000, 0, 300 + (rng.NextDouble() * 3200)];
            result.Add(CoverageFixtures.Photo(centre, target, $"p{i:000}"));
        }

        return result;
    }

    private static List<CoverageVolume> Volumes() =>
    [
        CoverageFixtures.Block(1, 1200, 1300, 1000, 450, 250),
        CoverageFixtures.Block(2, 3800, 1800, 900, 500),
        Pyramid(3, "0", 2500, 2400, 900, 600),
        Roof(4, "0", 1000, 2900, 1100, 450),
        Pyramid(5, "1", 1500, 1500, 1000, 700),
        Roof(6, "1", 1500, 2700, 1000, 500),
        CoverageFixtures.Block(7, 1500, 1200, 900, 400, 200) with { FacetId = "2" },
        CoverageFixtures.Block(8, 3500, 1300, 1000, 500) with { FacetId = "2" },
        Roof(9, "0", 4300, 800, 1000, 450),
        Pyramid(10, "2", 2500, 1500, 800, 350),
    ];

    private static CoverageVolume Pyramid(int index, string facet, double a, double b, double size, double height)
    {
        double h = size / 2;
        (double A, double B, double H)[] corners = [(a - h, b - h, 0), (a + h, b - h, 0), (a + h, b + h, 0), (a - h, b + h, 0)];
        var apex = (a, b, height);
        var faces = Enumerable.Range(0, 4).Select(i => new[] { corners[i], corners[(i + 1) % 4], apex }).ToList();
        return Flat(index, facet, faces, "pyramid");
    }

    private static CoverageVolume Roof(int index, string facet, double a, double b, double size, double height)
    {
        double h = size / 2;
        (double A, double B, double H)[] c = [(a - h, b - h, 0), (a + h, b - h, 0), (a + h, b + h, 0), (a - h, b + h, 0)];
        (double A, double B, double H)[] r = [(a - h, b, height), (a + h, b, height)];
        return Flat(
            index,
            facet,
            [[c[0], c[1], r[1], r[0]], [c[2], c[3], r[0], r[1]], [c[0], r[0], c[3]], [c[1], c[2], r[1]]],
            "roof");
    }

    private static CoverageVolume Flat(int index, string facet, List<(double A, double B, double H)[]> faces, string shape)
    {
        var surface = VolumeSurface.FlatSided(new VolumePolyhedron(faces, shape), 20);
        var g = surface.Grid;
        IReadOnlyList<double[]> footprint = [[g.ALo, g.BLo], [g.ALo + (g.Cols * g.CellMm), g.BLo + (g.Rows * g.CellMm)]];
        return new CoverageVolume(index, facet, surface, footprint);
    }
}
