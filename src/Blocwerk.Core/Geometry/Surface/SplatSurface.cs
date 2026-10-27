// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Geometry.Surface;

/// <summary>
/// The photo-real view's surface as a point set (splat centres, world mm) in a uniform grid, for one question: where does a
/// viewing ray first meet the surface near the wall? The hit is the <see cref="FrontPercentile"/> depth of the points within
/// <see cref="RadiusMm"/> of the ray (a low percentile: the side the camera sees, robust to a stray floater in front).
/// </summary>
public sealed class SplatSurface
{
    /// <summary>Points within this distance of a ray count as on it, mm (also the grid cell size).</summary>
    public const double RadiusMm = 6;

    /// <summary>The depth percentile taken as the front surface.</summary>
    public const double FrontPercentile = 0.3;

    /// <summary>Fewer points than this along a ray is a miss.</summary>
    public const int MinPoints = 5;

    /// <summary>The points (x, y, z, mm), sorted by grid cell: the only copy kept.</summary>
    private readonly float[] xyz;
    private readonly Dictionary<long, (int Start, int Count)> cells;

    /// <summary>
    /// Initializes a new instance of the <see cref="SplatSurface"/> class: indexes <paramref name="points"/> (world mm). It keeps
    /// one compact copy, sorted by cell (12 bytes a point plus the cell table); the caller can drop its list afterwards.
    /// </summary>
    public SplatSurface(IReadOnlyList<(float X, float Y, float Z)> points)
    {
        var keys = new long[points.Count];
        var order = new int[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            keys[i] = Key(Cell(points[i].X), Cell(points[i].Y), Cell(points[i].Z));
            order[i] = i;
        }

        Array.Sort(keys, order);
        xyz = new float[points.Count * 3];
        for (var k = 0; k < order.Length; k++)
        {
            (xyz[3 * k], xyz[(3 * k) + 1], xyz[(3 * k) + 2]) = points[order[k]];
        }

        cells = [];
        for (var i = 0; i < keys.Length;)
        {
            var j = i;
            while (j < keys.Length && keys[j] == keys[i])
            {
                j++;
            }

            cells[keys[i]] = (i, j - i);
            i = j;
        }
    }

    /// <summary>Gets how many points are indexed.</summary>
    public int Count => xyz.Length / 3;

    /// <summary>
    /// The front-surface hit of the ray from <paramref name="origin"/> along unit <paramref name="dir"/>, searched between
    /// depths <paramref name="near"/> and <paramref name="far"/> (mm); null when fewer than <see cref="MinPoints"/> lie on it.
    /// </summary>
    public double[]? Hit(double[] origin, double[] dir, double near, double far)
    {
        near = Math.Max(0, near);
        var depths = new List<double>();
        var seen = new HashSet<long>();
        for (var s = near; s <= far + RadiusMm; s += RadiusMm)
        {
            int cx = Cell(origin[0] + (s * dir[0])), cy = Cell(origin[1] + (s * dir[1])), cz = Cell(origin[2] + (s * dir[2]));
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        var key = Key(cx + dx, cy + dy, cz + dz);
                        if (seen.Add(key) && cells.TryGetValue(key, out var cell))
                        {
                            Collect(cell, origin, dir, near, far, depths);
                        }
                    }
                }
            }
        }

        if (depths.Count < MinPoints)
        {
            return null;
        }

        depths.Sort();
        var front = depths[(int)Math.Floor(FrontPercentile * (depths.Count - 1))];
        return [origin[0] + (front * dir[0]), origin[1] + (front * dir[1]), origin[2] + (front * dir[2])];
    }

    private static int Cell(double v) => (int)Math.Floor(v / RadiusMm);

    private static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

    private void Collect((int Start, int Count) cell, double[] o, double[] d, double near, double far, List<double> depths)
    {
        for (var k = cell.Start; k < cell.Start + cell.Count; k++)
        {
            var i = 3 * k;
            double rx = xyz[i] - o[0], ry = xyz[i + 1] - o[1], rz = xyz[i + 2] - o[2];
            var t = (rx * d[0]) + (ry * d[1]) + (rz * d[2]);
            if (t < near || t > far)
            {
                continue;
            }

            double px = rx - (t * d[0]), py = ry - (t * d[1]), pz = rz - (t * d[2]);
            if ((px * px) + (py * py) + (pz * pz) <= RadiusMm * RadiusMm)
            {
                depths.Add(t);
            }
        }
    }
}
