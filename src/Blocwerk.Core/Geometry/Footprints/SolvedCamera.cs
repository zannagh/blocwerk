// <copyright file="SolvedCamera.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Footprints;

/// <summary>
/// One capture photo's solved camera from the geometry model's <c>cameras</c> array: pinhole
/// intrinsics <c>K</c> (row-major 3×3, pixels), OpenCV distortion (k1, k2, p1, p2[, k3]) and the
/// world→camera pose <c>x_cam = R·X + t</c> (world millimetres).
/// </summary>
/// <param name="Image">The photo's name towards the geometry worker, e.g. <c>p01</c> (<see cref="Capture.CaptureComputeDocuments.PhotoName"/>).</param>
/// <param name="Width">Pixel width the intrinsics refer to.</param>
/// <param name="Height">Pixel height the intrinsics refer to.</param>
/// <param name="K">Intrinsics, row-major.</param>
/// <param name="Dist">Distortion coefficients.</param>
/// <param name="R">Rotation, row-major.</param>
/// <param name="T">Translation.</param>
public sealed record SolvedCamera(string Image, int Width, int Height, double[] K, double[] Dist, double[] R, double[] T)
{
    /// <summary>The camera centre in world millimetres (−Rᵀt).</summary>
    public double[] Centre =>
    [
        -((R[0] * T[0]) + (R[3] * T[1]) + (R[6] * T[2])),
        -((R[1] * T[0]) + (R[4] * T[1]) + (R[7] * T[2])),
        -((R[2] * T[0]) + (R[5] * T[1]) + (R[8] * T[2])),
    ];

    /// <summary>Every well-formed camera of a geometry model JSON; empty when it has none.</summary>
    /// <param name="modelJson">The model JSON.</param>
    /// <returns>The cameras.</returns>
    public static IReadOnlyList<SolvedCamera> ParseAll(string modelJson)
    {
        using var doc = JsonDocument.Parse(modelJson);
        if (!doc.RootElement.TryGetProperty("cameras", out var cams) || cams.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<SolvedCamera>();
        foreach (var c in cams.EnumerateArray())
        {
            var k = Numbers(c, "K");
            var r = Numbers(c, "R");
            var t = Numbers(c, "t");
            if (k.Length != 9 || r.Length != 9 || t.Length != 3 || !c.TryGetProperty("image", out var img))
            {
                continue;
            }

            var dist = Numbers(c, "dist");
            result.Add(new SolvedCamera(img.GetString() ?? string.Empty, Int(c, "width"), Int(c, "height"), k, dist, r, t));
        }

        return result;
    }

    /// <summary>The same camera for a photo stored at another resolution (intrinsics scaled per axis).</summary>
    /// <param name="width">Actual pixel width.</param>
    /// <param name="height">Actual pixel height.</param>
    /// <returns>The rescaled camera.</returns>
    public SolvedCamera ScaledTo(int width, int height)
    {
        if (Width <= 0 || Height <= 0 || (width == Width && height == Height))
        {
            return this;
        }

        var sx = (double)width / Width;
        var sy = (double)height / Height;
        return this with { Width = width, Height = height, K = [K[0] * sx, K[1] * sx, K[2] * sx, K[3], K[4] * sy, K[5] * sy, 0, 0, 1] };
    }

    /// <summary>The pixel of a world point, or null when it lies behind the camera.</summary>
    /// <param name="world">World point, mm.</param>
    /// <returns>The distorted pixel position.</returns>
    public (double X, double Y)? Project(double[] world)
    {
        var xc = (R[0] * world[0]) + (R[1] * world[1]) + (R[2] * world[2]) + T[0];
        var yc = (R[3] * world[0]) + (R[4] * world[1]) + (R[5] * world[2]) + T[1];
        var zc = (R[6] * world[0]) + (R[7] * world[1]) + (R[8] * world[2]) + T[2];
        if (zc <= 1e-6)
        {
            return null;
        }

        var (xd, yd) = Distort(xc / zc, yc / zc);
        return ((K[0] * xd) + (K[1] * yd) + K[2], (K[4] * yd) + K[5]);
    }

    /// <summary>The pixel's viewing ray in world coordinates: unit direction from <see cref="Centre"/>.</summary>
    /// <param name="px">Pixel x.</param>
    /// <param name="py">Pixel y.</param>
    /// <returns>The direction.</returns>
    public double[] RayDirection(double px, double py)
    {
        var yd = (py - K[5]) / K[4];
        var xd = (px - K[2] - (K[1] * yd)) / K[0];
        var (x, y) = Undistort(xd, yd);
        double[] d = [(R[0] * x) + (R[3] * y) + R[6], (R[1] * x) + (R[4] * y) + R[7], (R[2] * x) + (R[5] * y) + R[8]];
        var len = Math.Sqrt((d[0] * d[0]) + (d[1] * d[1]) + (d[2] * d[2]));
        return [d[0] / len, d[1] / len, d[2] / len];
    }

    /// <summary>Where the pixel's viewing ray meets the facet plane (a, b mm), or null when it misses.</summary>
    /// <param name="frame">The facet.</param>
    /// <param name="px">Pixel x.</param>
    /// <param name="py">Pixel y.</param>
    /// <returns>The plane point.</returns>
    public (double A, double B)? PixelToPlane(FacetFrame frame, double px, double py)
    {
        var yd = (py - K[5]) / K[4];
        var xd = (px - K[2] - (K[1] * yd)) / K[0];
        var (x, y) = Undistort(xd, yd);

        // Ray direction in world: Rᵀ·(x, y, 1).
        double[] d = [(R[0] * x) + (R[3] * y) + R[6], (R[1] * x) + (R[4] * y) + R[7], (R[2] * x) + (R[5] * y) + R[8]];
        var c = Centre;
        var n = frame.Normal;
        var denom = (d[0] * n[0]) + (d[1] * n[1]) + (d[2] * n[2]);
        if (Math.Abs(denom) < 1e-9)
        {
            return null;
        }

        var s = (((frame.Origin[0] - c[0]) * n[0]) + ((frame.Origin[1] - c[1]) * n[1]) + ((frame.Origin[2] - c[2]) * n[2])) / denom;
        if (s <= 0)
        {
            return null;
        }

        double[] rel = [c[0] + (s * d[0]) - frame.Origin[0], c[1] + (s * d[1]) - frame.Origin[1], c[2] + (s * d[2]) - frame.Origin[2]];
        return ((rel[0] * frame.U[0]) + (rel[1] * frame.U[1]) + (rel[2] * frame.U[2]), (rel[0] * frame.V[0]) + (rel[1] * frame.V[1]) + (rel[2] * frame.V[2]));
    }

    private (double X, double Y) Distort(double x, double y)
    {
        double k1 = D(0), k2 = D(1), p1 = D(2), p2 = D(3), k3 = D(4);
        var r2 = (x * x) + (y * y);
        var radial = 1 + (k1 * r2) + (k2 * r2 * r2) + (k3 * r2 * r2 * r2);
        return ((x * radial) + (2 * p1 * x * y) + (p2 * (r2 + (2 * x * x))), (y * radial) + (p1 * (r2 + (2 * y * y))) + (2 * p2 * x * y));
    }

    /// <summary>Fixed-point inverse of <see cref="Distort"/>; the solver's distortion is mild.</summary>
    private (double X, double Y) Undistort(double xd, double yd)
    {
        var (x, y) = (xd, yd);
        for (var i = 0; i < 8; i++)
        {
            var (dx, dy) = Distort(x, y);
            x += xd - dx;
            y += yd - dy;
        }

        return (x, y);
    }

    private double D(int i) => i < Dist.Length ? Dist[i] : 0;

    private static double[] Numbers(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToArray()
            : [];

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
