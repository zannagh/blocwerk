// <copyright file="VolumeSurfaceBuilder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>
/// Builds a <see cref="VolumeSurface"/> from a facet cloud over a volume's footprint: per cell a high percentile
/// of the heights (splat centres scatter INTO the body, the surface is their outer envelope), empty cells filled
/// from the nearest measured cell, a 3×3 median against single noisy cells, 0 outside the footprint grown by one
/// cell. Heights are relative to the wall's fitted surface, so the base meets the wall at 0.
/// </summary>
public static class VolumeSurfaceBuilder
{
    private const int PadCells = 3;

    /// <summary>The surface over <paramref name="footprint"/>.</summary>
    /// <param name="cloud">The facet's cloud.</param>
    /// <param name="footprint">The volume's convex outline on the facet.</param>
    /// <param name="options">Tuning.</param>
    /// <returns>The surface.</returns>
    public static VolumeSurface Build(FacetCloud cloud, IReadOnlyList<(double A, double B)> footprint, VolumeDetectionOptions options)
    {
        var cell = options.CellMm;
        var pad = PadCells * cell;
        var grid = CellGrid.Covering(
            footprint.Min(p => p.A) - pad, footprint.Max(p => p.A) + pad, footprint.Min(p => p.B) - pad, footprint.Max(p => p.B) + pad, cell);
        var raw = grid.Quantile(cloud.A, cloud.B, cloud.H, options.SurfaceQuantile, 2);
        var inside = new bool[raw.Length];
        for (var k = 0; k < raw.Length; k++)
        {
            var (a, b) = grid.Centre(k);
            inside[k] = PlanePolygon.Contains(footprint, (a, b));
        }

        inside = grid.Dilate(inside, -1, 1);
        var filled = FillNearest(grid, raw);
        var smooth = Median3(grid, filled);
        var heights = new short[raw.Length];
        for (var k = 0; k < raw.Length; k++)
        {
            heights[k] = inside[k] ? (short)Math.Clamp(Math.Round(smooth[k]), 0, short.MaxValue) : (short)0;
        }

        return new VolumeSurface(grid, heights);
    }

    /// <summary>Every NaN cell takes the value of its nearest (breadth-first) measured cell.</summary>
    private static double[] FillNearest(CellGrid grid, double[] values)
    {
        var result = (double[])values.Clone();
        var queue = new Queue<int>();
        for (var k = 0; k < result.Length; k++)
        {
            if (!double.IsNaN(result[k]))
            {
                queue.Enqueue(k);
            }
        }

        if (queue.Count == 0)
        {
            Array.Fill(result, 0);
            return result;
        }

        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            int i = c % grid.Cols, j = c / grid.Cols;
            foreach (var n in Neighbours(grid, i, j))
            {
                if (double.IsNaN(result[n]))
                {
                    result[n] = result[c];
                    queue.Enqueue(n);
                }
            }
        }

        return result;
    }

    private static IEnumerable<int> Neighbours(CellGrid grid, int i, int j)
    {
        if (i > 0)
        {
            yield return (j * grid.Cols) + i - 1;
        }

        if (i < grid.Cols - 1)
        {
            yield return (j * grid.Cols) + i + 1;
        }

        if (j > 0)
        {
            yield return ((j - 1) * grid.Cols) + i;
        }

        if (j < grid.Rows - 1)
        {
            yield return ((j + 1) * grid.Cols) + i;
        }
    }

    private static double[] Median3(CellGrid grid, double[] values)
    {
        var result = new double[values.Length];
        var window = new List<double>(9);
        for (var j = 0; j < grid.Rows; j++)
        {
            for (var i = 0; i < grid.Cols; i++)
            {
                window.Clear();
                for (var dj = -1; dj <= 1; dj++)
                {
                    for (var di = -1; di <= 1; di++)
                    {
                        // Edge cells repeat the nearest row/column (scipy's "reflect" is close enough here).
                        var ni = Math.Clamp(i + di, 0, grid.Cols - 1);
                        var nj = Math.Clamp(j + dj, 0, grid.Rows - 1);
                        window.Add(values[(nj * grid.Cols) + ni]);
                    }
                }

                window.Sort();
                result[(j * grid.Cols) + i] = window[4];
            }
        }

        return result;
    }
}
