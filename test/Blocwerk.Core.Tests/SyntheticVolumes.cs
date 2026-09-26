// <copyright file="SyntheticVolumes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Height fields of flat-sheet volumes as the detector stores them: a 20 mm grid with noise and missing cells (0), the
/// footprint the convex hull of the cells higher than 35 mm, the surface 0 beyond that footprint grown by one cell.
/// </summary>
internal static class SyntheticVolumes
{
    private const double Cell = 20;

    /// <summary>A 400 × 350 mm pyramid on [1200, 1600] × [800, 1150], apex (1400, 975) at 150 mm.</summary>
    public static double Pyramid(double a, double b)
    {
        double h = 150, ca = 1400, cb = 975;
        var ua = a <= ca ? (a - 1200) / (ca - 1200) : (1600 - a) / (1600 - ca);
        var ub = b <= cb ? (b - 800) / (cb - 800) : (1150 - b) / (1150 - cb);
        return Math.Max(0, h * Math.Min(ua, ub));
    }

    /// <summary>An 800 × 400 mm hip roof on [1000, 1800] × [800, 1200], ridge (1200, 1000)–(1600, 1000) at 140 mm.</summary>
    public static double Roof(double a, double b)
    {
        var h = 140 * Math.Min(Math.Min((b - 800) / 200, (1200 - b) / 200), Math.Min((a - 1000) / 200, (1800 - a) / 200));
        return Math.Max(0, h);
    }

    /// <summary>The stored height field and footprint of a shape (±<paramref name="noiseMm"/>, <paramref name="missing"/> of the cells lost).</summary>
    public static (VolumeSurface Field, List<(double A, double B)> Footprint) Detected(
        Func<double, double, double> shape, double aLo, double aHi, double bLo, double bHi, double noiseMm = 6, double missing = 0.05, int seed = 11)
    {
        var rng = new Random(seed);
        var grid = CellGrid.Covering(aLo - 100, aHi + 100, bLo - 100, bHi + 100, Cell);
        var measured = new double[grid.Cols * grid.Rows];
        var corners = new List<(double A, double B)>();
        for (var k = 0; k < measured.Length; k++)
        {
            var (a, b) = grid.Centre(k);
            measured[k] = shape(a, b) + ((rng.NextDouble() - 0.5) * 2 * noiseMm);
            if (measured[k] > 35)
            {
                corners.AddRange([(a - 10, b - 10), (a + 10, b - 10), (a - 10, b + 10), (a + 10, b + 10)]);
            }
        }

        var footprint = PlanePolygon.ConvexHull(corners);
        var inside = measured.Select((_, k) => PlanePolygon.Contains(footprint, grid.Centre(k))).ToArray();
        inside = grid.Dilate(inside, -1, 1);
        var heights = new short[measured.Length];
        for (var k = 0; k < heights.Length; k++)
        {
            var lost = rng.NextDouble() < missing;
            heights[k] = inside[k] && !lost ? (short)Math.Clamp(Math.Round(measured[k]), 0, 1000) : (short)0;
        }

        return (new VolumeSurface(grid, heights), footprint);
    }

    /// <summary>Distance in the plane.</summary>
    public static double Dist((double A, double B) p, (double A, double B) q) => Math.Sqrt(((p.A - q.A) * (p.A - q.A)) + ((p.B - q.B) * (p.B - q.B)));
}
