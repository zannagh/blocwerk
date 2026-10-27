// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Buffers.Binary;
using System.IO.Compression;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Footprints;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A synthetic photo-real scene on <see cref="HoldPlacementScenario.TwoFacetJson"/>'s facet 0 (the plane y = 0, a = x, b = z,
/// out of the wall = −y): the wall as a 5 mm point grid, holds as 60 × 60 mm boxes standing 40 mm out, and look-at cameras.
/// </summary>
internal static class SurfaceScene
{
    public const double HoldHeightMm = 40;
    public const double HoldHalfMm = 30;

    /// <summary>The wall grid over a ∈ [a0, a1], b ∈ [b0, b1] plus a box hold at each base centre.</summary>
    public static List<(float X, float Y, float Z)> Points(double a0, double a1, double b0, double b1, params (double A, double B)[] holds)
    {
        var points = new List<(float, float, float)>();
        for (var a = a0; a <= a1; a += 5)
        {
            for (var b = b0; b <= b1; b += 5)
            {
                if (!holds.Any(h => Math.Abs(a - h.A) <= HoldHalfMm && Math.Abs(b - h.B) <= HoldHalfMm))
                {
                    points.Add(((float)a, 0f, (float)b));
                }
            }
        }

        foreach (var (ha, hb) in holds)
        {
            for (var u = -HoldHalfMm; u <= HoldHalfMm; u += 3)
            {
                for (var w = -HoldHalfMm; w <= HoldHalfMm; w += 3)
                {
                    points.Add(((float)(ha + u), (float)-HoldHeightMm, (float)(hb + w)));
                }

                for (var d = 0.0; d <= HoldHeightMm; d += 3)
                {
                    points.Add(((float)(ha + u), (float)-d, (float)(hb - HoldHalfMm)));
                    points.Add(((float)(ha + u), (float)-d, (float)(hb + HoldHalfMm)));
                    points.Add(((float)(ha - HoldHalfMm), (float)-d, (float)(hb + u)));
                    points.Add(((float)(ha + HoldHalfMm), (float)-d, (float)(hb + u)));
                }
            }
        }

        return points;
    }

    /// <summary>A 1000 × 1000 px pinhole camera at <paramref name="centre"/> looking at <paramref name="target"/>, z up.</summary>
    public static SolvedCamera LookAt(string name, double[] centre, double[] target, double focal = 1000)
    {
        var f = Unit([target[0] - centre[0], target[1] - centre[1], target[2] - centre[2]]);
        var right = Unit(Cross(f, [0, 0, 1]));
        var down = Cross(f, right);
        double[] r = [.. right, .. down, .. f];
        double[] t = [-Dot(right, centre), -Dot(down, centre), -Dot(f, centre)];
        return new SolvedCamera(name, 1000, 1000, [focal, 0, 500, 0, focal, 500, 0, 0, 1], [0, 0, 0, 0], r, t);
    }

    /// <summary>The normalised photo position of a world point.</summary>
    public static (double X, double Y) Seen(SolvedCamera camera, double[] world)
    {
        var (px, py) = camera.Project(world)!.Value;
        return (px / camera.Width, py / camera.Height);
    }

    /// <summary>A hold whose centre is the box top's centre as <paramref name="camera"/> sees it, with a box outline.</summary>
    public static Hold HoldSeenBy(SolvedCamera camera, Guid panel, double a, double b)
    {
        var (x, y) = Seen(camera, [a, -HoldHeightMm, b]);
        var ring = new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) }
            .Select(c => Seen(camera, [a + (c.Item1 * HoldHalfMm), -HoldHeightMm / 2, b + (c.Item2 * HoldHalfMm)]))
            .Select(p => new ShapePoint { Dx = p.X - x, Dy = p.Y - y })
            .ToList();
        return new Hold { WallPanelId = panel, X = x, Y = y, Radius = 0.01, ShapePoints = ring, Color = "blue" };
    }

    /// <summary>The points as an SPZ v2 scene in metres (pair with <see cref="FrameJson"/>): opaque, 4 mm splats.</summary>
    public static byte[] Spz(IReadOnlyList<(float X, float Y, float Z)> points)
    {
        var n = points.Count;
        var raw = new byte[16 + (n * 19)];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, 0x5053474e);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(8), (uint)n);
        raw[13] = 12;
        for (var i = 0; i < n; i++)
        {
            var (x, y, z) = points[i];
            Fixed24(raw, 16 + (9 * i), x / 1000);
            Fixed24(raw, 16 + (9 * i) + 3, y / 1000);
            Fixed24(raw, 16 + (9 * i) + 6, z / 1000);
            raw[16 + (9 * n) + i] = 255;
            var scale = (byte)((Math.Log(0.004) + 10) * 16);
            raw[16 + (13 * n) + (3 * i)] = raw[16 + (13 * n) + (3 * i) + 1] = raw[16 + (13 * n) + (3 * i) + 2] = scale;
        }

        using var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw);
        }

        return gz.ToArray();
    }

    /// <summary>A splat frame whose (refined) world matrix turns the SPZ's metres into the wall world's millimetres.</summary>
    public const string FrameJson = """{ "toWorldMm": [[1000, 0, 0, 0], [0, 1000, 0, 0], [0, 0, 1000, 0], [0, 0, 0, 1]] }""";

    private static void Fixed24(byte[] raw, int at, double value)
    {
        var v = (int)Math.Round(value * 4096) & 0xFFFFFF;
        (raw[at], raw[at + 1], raw[at + 2]) = ((byte)v, (byte)(v >> 8), (byte)(v >> 16));
    }

    private static double[] Unit(double[] v)
    {
        var l = Math.Sqrt(Dot(v, v));
        return [v[0] / l, v[1] / l, v[2] / l];
    }

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);
}
