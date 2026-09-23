// <copyright file="Vec3.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Registration;

/// <summary>Minimal 3-vector helpers on <c>double[3]</c>, the shape wall-geometry.json uses.</summary>
internal static class Vec3
{
    public static double[] Add(double[] a, double[] b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];

    public static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    public static double[] Scale(double[] a, double s) => [a[0] * s, a[1] * s, a[2] * s];

    public static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    public static double Length(double[] a) => Math.Sqrt(Dot(a, a));

    public static double Distance(double[] a, double[] b) => Length(Sub(a, b));

    public static double[] Mean(IEnumerable<double[]> points)
    {
        double x = 0, y = 0, z = 0;
        var n = 0;
        foreach (var p in points)
        {
            (x, y, z, n) = (x + p[0], y + p[1], z + p[2], n + 1);
        }

        return n == 0 ? [0, 0, 0] : [x / n, y / n, z / n];
    }

    /// <summary>The angle between two directions, in degrees.</summary>
    public static double AngleDeg(double[] a, double[] b)
    {
        var la = Length(a);
        var lb = Length(b);
        return la < 1e-12 || lb < 1e-12 ? 180 : Math.Acos(Math.Clamp(Dot(a, b) / (la * lb), -1, 1)) * 180 / Math.PI;
    }
}
