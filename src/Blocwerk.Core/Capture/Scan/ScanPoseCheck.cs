// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Scan;

/// <summary>
/// Sanity of one frame's camera: a rigid 4 × 4 camera-to-world transform (column-major, as ARKit's <c>simd_float4x4</c>)
/// and pinhole intrinsics a phone camera can have at the video resolution.
/// </summary>
public static class ScanPoseCheck
{
    /// <summary>The farthest a camera may be from the session origin, in metres.</summary>
    public const double MaxTranslation = 100;

    private const double Tolerance = 1e-3;

    /// <summary>What is wrong with <paramref name="m"/>, or null for a rigid transform.</summary>
    public static string? Problem(double[]? m)
    {
        if (m is not { Length: 16 } || m.Any(v => !double.IsFinite(v)))
        {
            return "needs 16 finite numbers (column-major camera-to-world).";
        }

        if (Math.Abs(m[3]) > Tolerance || Math.Abs(m[7]) > Tolerance || Math.Abs(m[11]) > Tolerance || Math.Abs(m[15] - 1) > Tolerance)
        {
            return "the bottom row must be 0 0 0 1 (is it row-major?).";
        }

        double[] x = [m[0], m[1], m[2]], y = [m[4], m[5], m[6]], z = [m[8], m[9], m[10]];
        if (!Unit(x) || !Unit(y) || !Unit(z) || Math.Abs(Dot(x, y)) > Tolerance || Math.Abs(Dot(y, z)) > Tolerance
            || Math.Abs(Dot(x, z)) > Tolerance || Math.Abs(Dot(Cross(x, y), z) - 1) > Tolerance)
        {
            return "the rotation is not orthonormal with determinant +1.";
        }

        return Math.Sqrt((m[12] * m[12]) + (m[13] * m[13]) + (m[14] * m[14])) > MaxTranslation
            ? $"the camera is more than {MaxTranslation} m from the origin."
            : null;
    }

    /// <summary>What is wrong with the intrinsics for a <paramref name="width"/> × <paramref name="height"/> frame, or null.</summary>
    public static string? IntrinsicsProblem(ScanIntrinsics? k, int width, int height)
    {
        if (k is not { Fx: { } fx, Fy: { } fy, Cx: { } cx, Cy: { } cy })
        {
            return "fx, fy, cx and cy are required.";
        }

        if (fx < 0.3 * width || fx > 3 * width || fy < 0.3 * width || fy > 3 * width || Math.Abs(fx - fy) > 0.05 * fx)
        {
            return $"focal lengths {fx}/{fy} px are not a phone camera's at {width} px width.";
        }

        return cx < 0.25 * width || cx > 0.75 * width || cy < 0.25 * height || cy > 0.75 * height
            ? $"principal point ({cx}, {cy}) is not near the centre of {width}×{height}."
            : null;
    }

    private static bool Unit(double[] v) => Math.Abs(Math.Sqrt(Dot(v, v)) - 1) <= Tolerance;

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];
}
