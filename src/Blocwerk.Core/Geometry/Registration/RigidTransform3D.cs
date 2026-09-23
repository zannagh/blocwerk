// <copyright file="RigidTransform3D.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Registration;

/// <summary>
/// A 3D rotation + translation (no scale, no mirror): <c>x' = R·x + t</c>. Fitted in the least-squares
/// sense with Horn's closed-form quaternion method (the eigenvector of the largest eigenvalue of a 4×4
/// symmetric matrix), which needs no SVD and never returns a reflection.
/// </summary>
public sealed class RigidTransform3D
{
    private RigidTransform3D(double[,] rotation, double[] translation)
    {
        Rotation = rotation;
        Translation = translation;
    }

    /// <summary>The rotation, row-major 3×3.</summary>
    public double[,] Rotation { get; }

    /// <summary>The translation (mm).</summary>
    public double[] Translation { get; }

    /// <summary>The rotation angle, in degrees.</summary>
    public double RotationDeg =>
        Math.Acos(Math.Clamp((Rotation[0, 0] + Rotation[1, 1] + Rotation[2, 2] - 1) / 2, -1, 1)) * 180 / Math.PI;

    /// <summary>The length of the translation, in mm.</summary>
    public double TranslationMm => Vec3.Length(Translation);

    /// <summary>The identity transform.</summary>
    public static RigidTransform3D Identity { get; } = new(
        new double[,]
        {
            { 1, 0, 0 },
            { 0, 1, 0 },
            { 0, 0, 1 },
        },
        [0, 0, 0]);

    /// <summary>Builds a transform from a rotation and a translation.</summary>
    public static RigidTransform3D From(double[,] rotation, double[] translation) =>
        new((double[,])rotation.Clone(), (double[])translation.Clone());

    /// <summary>Maps a point.</summary>
    public double[] Apply(double[] p) => Vec3.Add(Rotate(p), Translation);

    /// <summary>Rotates a direction (no translation).</summary>
    public double[] Rotate(double[] v) =>
    [
        (Rotation[0, 0] * v[0]) + (Rotation[0, 1] * v[1]) + (Rotation[0, 2] * v[2]),
        (Rotation[1, 0] * v[0]) + (Rotation[1, 1] * v[1]) + (Rotation[1, 2] * v[2]),
        (Rotation[2, 0] * v[0]) + (Rotation[2, 1] * v[1]) + (Rotation[2, 2] * v[2]),
    ];

    /// <summary>Least-squares fit mapping every <c>From</c> onto its <c>To</c>; null for fewer than 3 pairs.</summary>
    public static RigidTransform3D? Fit(IReadOnlyList<(double[] From, double[] To)> pairs) =>
        Fit(pairs.Select(p => (p.From, p.To, 1.0)).ToList());

    /// <summary>
    /// Weighted least-squares fit (minimises Σ w·|R·from + t − to|²); null for fewer than 3 pairs or no
    /// positive weight.
    /// </summary>
    public static RigidTransform3D? Fit(IReadOnlyList<(double[] From, double[] To, double Weight)> pairs)
    {
        var total = pairs.Sum(p => Math.Max(0, p.Weight));
        if (pairs.Count < 3 || total <= 0)
        {
            return null;
        }

        var cFrom = WeightedMean(pairs.Select(p => (p.From, p.Weight)), total);
        var cTo = WeightedMean(pairs.Select(p => (p.To, p.Weight)), total);
        var s = new double[3, 3];
        foreach (var (from, to, weight) in pairs)
        {
            var w = Math.Max(0, weight);
            var a = Vec3.Sub(from, cFrom);
            var b = Vec3.Sub(to, cTo);
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    s[i, j] += w * a[i] * b[j];
                }
            }
        }

        var q = LargestEigenvector(HornMatrix(s));
        var rotation = FromQuaternion(q[0], q[1], q[2], q[3]);
        var fitted = new RigidTransform3D(rotation, [0, 0, 0]);
        return new RigidTransform3D(rotation, Vec3.Sub(cTo, fitted.Rotate(cFrom)));
    }

    private static double[] WeightedMean(IEnumerable<(double[] Point, double Weight)> points, double total)
    {
        var sum = new double[3];
        foreach (var (p, weight) in points)
        {
            var w = Math.Max(0, weight);
            for (var i = 0; i < 3; i++)
            {
                sum[i] += w * p[i] / total;
            }
        }

        return sum;
    }

    private static double[,] HornMatrix(double[,] s)
    {
        double sxx = s[0, 0], sxy = s[0, 1], sxz = s[0, 2];
        double syx = s[1, 0], syy = s[1, 1], syz = s[1, 2];
        double szx = s[2, 0], szy = s[2, 1], szz = s[2, 2];
        return new double[,]
        {
            { sxx + syy + szz, syz - szy, szx - sxz, sxy - syx },
            { syz - szy, sxx - syy - szz, sxy + syx, szx + sxz },
            { szx - sxz, sxy + syx, -sxx + syy - szz, syz + szy },
            { sxy - syx, szx + sxz, syz + szy, -sxx - syy + szz },
        };
    }

    private static double[] LargestEigenvector(double[,] n)
    {
        var (values, vectors) = SymmetricEigen.Decompose(n);
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best])
            {
                best = i;
            }
        }

        return [vectors[0, best], vectors[1, best], vectors[2, best], vectors[3, best]];
    }

    private static double[,] FromQuaternion(double w, double x, double y, double z)
    {
        var norm = Math.Sqrt((w * w) + (x * x) + (y * y) + (z * z));
        (w, x, y, z) = (w / norm, x / norm, y / norm, z / norm);
        return new double[,]
        {
            { 1 - (2 * ((y * y) + (z * z))), 2 * ((x * y) - (w * z)), 2 * ((x * z) + (w * y)) },
            { 2 * ((x * y) + (w * z)), 1 - (2 * ((x * x) + (z * z))), 2 * ((y * z) - (w * x)) },
            { 2 * ((x * z) - (w * y)), 2 * ((y * z) + (w * x)), 1 - (2 * ((x * x) + (y * y))) },
        };
    }
}
