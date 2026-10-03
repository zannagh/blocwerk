// <copyright file="CoverageFixtures.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Capture.Coverage;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Synthetic coverage geometry: one vertical 3 × 3 m facet "0" named "Wall" (a = world x, b = world z = height above
/// the floor, the climber side at negative y), pinhole cameras aimed at chosen points, and block volumes.
/// </summary>
internal static class CoverageFixtures
{
    /// <summary>The facet's model JSON with the given markers (id, centre a, centre b, photos).</summary>
    public static string DocumentJson(params (int Id, double A, double B, int Photos)[] markers)
    {
        var list = markers.Select(m => new
        {
            id = m.Id,
            segment = 0,
            facet = "0",
            cornersPlaneMm = new[]
            {
                new[] { m.A - 50, m.B + 50 }, new[] { m.A + 50, m.B + 50 }, new[] { m.A + 50, m.B - 50 }, new[] { m.A - 50, m.B - 50 },
            },
            observations = m.Photos,
        });
        var doc = new
        {
            version = 1,
            units = "mm",
            markerSizeMm = 100,
            world = new { up = new[] { 0.0, 0, 1 } },
            segments = new[]
            {
                new
                {
                    index = 0, name = "Wall", measuredAngleDeg = 0.0,
                    facets = new[]
                    {
                        new
                        {
                            id = "0", origin = new[] { 0.0, 0, 0 }, u = new[] { 1.0, 0, 0 }, v = new[] { 0.0, 0, 1 },
                            normal = new[] { 0.0, -1, 0 }, yawDeg = 0.0, extentMm = new { aMin = 0, aMax = 3000, bMin = 0, bMax = 3000 },
                        },
                    },
                },
            },
            markers = list,
        };
        return JsonSerializer.Serialize(doc);
    }

    /// <summary>The parsed document.</summary>
    public static WallGeometryDocument Document(params (int Id, double A, double B, int Photos)[] markers) =>
        WallGeometryDocument.Parse(DocumentJson(markers));

    /// <summary>A pinhole camera at <paramref name="centre"/> looking at <paramref name="target"/> (4000 × 3000 px, f = 2000 px).</summary>
    public static SolvedCamera LookAt(string name, double[] centre, double[] target)
    {
        var z = Unit([target[0] - centre[0], target[1] - centre[1], target[2] - centre[2]]);
        var x = Unit(Cross(z, Math.Abs(z[2]) > 0.99 ? [0, 1, 0] : [0, 0, 1]));
        var y = Cross(z, x);
        double[] r = [.. x, .. y, .. z];
        double[] t = [-Dot(x, centre), -Dot(y, centre), -Dot(z, centre)];
        return new SolvedCamera(name, 4000, 3000, [2000, 0, 2000, 0, 2000, 1500, 0, 0, 1], [], r, t);
    }

    /// <summary>A capture photo camera.</summary>
    public static CoverageCamera Photo(double[] centre, double[] target, string name = "p01") => new(LookAt(name, centre, target), false);

    /// <summary>A photo-real <c>frame.json</c> reporting registered video frames with these poses.</summary>
    public static string FrameJson(int registered, params SolvedCamera[] videoCameras)
    {
        var cams = videoCameras.Select(c => new { image = c.Image, width = c.Width, height = c.Height, K = c.K, R = c.R, t = c.T, dist = c.Dist });
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["stats"] = new { videoFrames = 300, videoFramesRegistered = registered },
            [CoverageCamera.VideoCamerasField] = cams,
        });
    }

    /// <summary>
    /// A block volume on facet "0": <paramref name="heightMm"/> tall over the square of side <paramref name="sizeMm"/>
    /// centred on (a, b), sloping to the wall over the outer <paramref name="slopeMm"/> (0 = sheer sides).
    /// </summary>
    public static CoverageVolume Block(int index, double a, double b, double sizeMm = 400, double heightMm = 300, double slopeMm = 0)
    {
        const double cell = 10;
        var n = (int)(sizeMm / cell) + 4;
        var grid = new CellGrid(a - (n * cell / 2), b - (n * cell / 2), cell, n, n);
        var heights = new short[n * n];
        for (var k = 0; k < heights.Length; k++)
        {
            var (ca, cb) = grid.Centre(k);
            var edge = (sizeMm / 2) - Math.Max(Math.Abs(ca - a), Math.Abs(cb - b));
            var h = edge < 0 ? 0 : slopeMm <= 0 ? heightMm : Math.Min(heightMm, heightMm * edge / slopeMm);
            heights[k] = (short)Math.Round(h);
        }

        double half = sizeMm / 2;
        IReadOnlyList<double[]> footprint = [[a - half, b - half], [a + half, b - half], [a + half, b + half], [a - half, b + half]];
        return new CoverageVolume(index, "0", new VolumeSurface(grid, heights), footprint);
    }

    /// <summary>The analyzer's inputs over the synthetic wall.</summary>
    public static CoverageInputs Inputs(
        WallGeometryDocument doc,
        IReadOnlyList<CoverageCamera> photos,
        IReadOnlyList<CoverageVolume>? volumes = null,
        IReadOnlyList<CoverageCamera>? videoFrames = null,
        CoverageVideoInput? video = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), doc, photos, videoFrames ?? [], volumes ?? [], new Dictionary<string, PlaneRectMm>(),
            video ?? new CoverageVideoInput(true, 300, 300));

    /// <summary>The scene of the synthetic wall.</summary>
    public static CoverageScene Scene(params CoverageVolume[] volumes) =>
        new(CaptureCoverageAnalyzer.Facets(Document(), new Dictionary<string, PlaneRectMm>()), volumes);

    /// <summary>The facet point (a, b) in world mm.</summary>
    public static double[] At(double a, double b) => [a, 0, b];

    /// <summary>The rated cell of facet "0" holding (a, b).</summary>
    public static CoverageCellStatus CellAt(CaptureCoverageReport report, double a, double b)
    {
        var f = report.Facets.Single(x => x.FacetId == "0");
        var i = (int)Math.Floor((a - f.ALo) / f.CellMm);
        var j = (int)Math.Floor((b - f.BLo) / f.CellMm);
        return CoverageCellCodes.Status(f.Cells[(j * f.Cols) + i]);
    }

    private static double[] Unit(double[] v)
    {
        var len = Math.Sqrt(Dot(v, v));
        return [v[0] / len, v[1] / len, v[2] / len];
    }

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);
}
