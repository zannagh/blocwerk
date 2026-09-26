// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Geometry.Corrections;

/// <summary>
/// A similarity of the wall-geometry world: <c>x' = s·Q·x + t</c> (uniform scale, rotation, translation; never a mirror).
/// What a correction of a model applies to its facets, cameras, textures and photo-real scene.
/// </summary>
/// <param name="Scale">The uniform scale <c>s</c> (&gt; 0).</param>
/// <param name="Rotation">The rotation <c>Q</c>, row-major 3×3.</param>
/// <param name="Translation">The translation <c>t</c>, mm.</param>
public sealed record GeometrySimilarity(double Scale, double[] Rotation, double[] Translation)
{
    /// <summary>The identity.</summary>
    public static GeometrySimilarity Identity { get; } = new(1, [1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]);

    /// <summary>The rotation angle, degrees.</summary>
    public double RotationDeg =>
        Math.Acos(Math.Clamp((Rotation[0] + Rotation[4] + Rotation[8] - 1) / 2, -1, 1)) * 180 / Math.PI;

    /// <summary>A uniform scale about the world origin.</summary>
    /// <param name="scale">The factor.</param>
    /// <returns>The similarity.</returns>
    public static GeometrySimilarity Scaling(double scale) => Identity with { Scale = scale };

    /// <summary>The smallest rotation turning direction <paramref name="from"/> into <paramref name="to"/>, about <paramref name="pivot"/>.</summary>
    /// <param name="from">The direction to turn (need not be unit).</param>
    /// <param name="to">Where it should point.</param>
    /// <param name="pivot">The point that stays put, mm.</param>
    /// <returns>The similarity (scale 1).</returns>
    public static GeometrySimilarity RotationBetween(double[] from, double[] to, double[] pivot)
    {
        var a = Unit(from);
        var b = Unit(to);
        var axis = Cross(a, b);
        var sin = Math.Sqrt(Dot(axis, axis));
        var cos = Math.Clamp(Dot(a, b), -1, 1);
        double[] q = sin < 1e-12 ? [1, 0, 0, 0, 1, 0, 0, 0, 1] : Rodrigues(Scale3(axis, 1 / sin), Math.Atan2(sin, cos));
        var rotated = Multiply(q, pivot);
        return new GeometrySimilarity(1, q, [pivot[0] - rotated[0], pivot[1] - rotated[1], pivot[2] - rotated[2]]);
    }

    /// <summary>Maps a point.</summary>
    /// <param name="p">World point, mm.</param>
    /// <returns>The mapped point.</returns>
    public double[] Apply(double[] p)
    {
        var r = Multiply(Rotation, p);
        return [(Scale * r[0]) + Translation[0], (Scale * r[1]) + Translation[1], (Scale * r[2]) + Translation[2]];
    }

    /// <summary>Rotates a direction (no scale, no translation).</summary>
    /// <param name="v">The direction.</param>
    /// <returns>The rotated direction.</returns>
    public double[] Rotate(double[] v) => Multiply(Rotation, v);

    /// <summary>The similarity as a row-major 4×4 matrix.</summary>
    /// <returns>16 numbers.</returns>
    public double[] ToMatrix4() =>
    [
        Scale * Rotation[0], Scale * Rotation[1], Scale * Rotation[2], Translation[0],
        Scale * Rotation[3], Scale * Rotation[4], Scale * Rotation[5], Translation[1],
        Scale * Rotation[6], Scale * Rotation[7], Scale * Rotation[8], Translation[2],
        0, 0, 0, 1,
    ];

    /// <summary>Row-major 3×3 times a 3-vector.</summary>
    internal static double[] Multiply(double[] m, double[] v) =>
    [
        (m[0] * v[0]) + (m[1] * v[1]) + (m[2] * v[2]),
        (m[3] * v[0]) + (m[4] * v[1]) + (m[5] * v[2]),
        (m[6] * v[0]) + (m[7] * v[1]) + (m[8] * v[2]),
    ];

    internal static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    internal static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];

    internal static double[] Unit(double[] v)
    {
        var length = Math.Sqrt(Dot(v, v));
        return length < 1e-12 ? [0, 0, 0] : Scale3(v, 1 / length);
    }

    private static double[] Scale3(double[] v, double s) => [v[0] * s, v[1] * s, v[2] * s];

    private static double[] Rodrigues(double[] k, double angle)
    {
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        var t = 1 - c;
        return
        [
            c + (k[0] * k[0] * t), (k[0] * k[1] * t) - (k[2] * s), (k[0] * k[2] * t) + (k[1] * s),
            (k[1] * k[0] * t) + (k[2] * s), c + (k[1] * k[1] * t), (k[1] * k[2] * t) - (k[0] * s),
            (k[2] * k[0] * t) - (k[1] * s), (k[2] * k[1] * t) + (k[0] * s), c + (k[2] * k[2] * t),
        ];
    }
}
