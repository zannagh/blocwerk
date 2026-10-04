// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>A triangular facet standing on the main wall's plane: its hypotenuse (two corners on it) and its apex.</summary>
internal sealed record RecessTriangle(Wall3DFacet Facet, double[][] Hypotenuse, double[] Apex)
{
    /// <summary>The triangle view of <paramref name="f"/>, or null when it is not one standing on <paramref name="main"/>'s plane.</summary>
    public static RecessTriangle? Of(Wall3DFacet f, Wall3DFacet main, double perpendicularDot, double onPlaneMm, double apexOffMm)
    {
        if (Math.Abs(Vec.Dot(f.Normal, main.Normal)) > perpendicularDot)
        {
            return null;
        }

        var offsets = f.Corners.Select(c => Vec.Dot(main.Normal, Vec.Sub(c, main.Origin))).ToArray();
        var on = Enumerable.Range(0, 3).Where(i => Math.Abs(offsets[i]) <= onPlaneMm).ToArray();
        var apex = Enumerable.Range(0, 3).Except(on).ToArray();
        if (on.Length != 2 || apex.Length != 1 || offsets[apex[0]] > -apexOffMm)
        {
            return null;
        }

        return new RecessTriangle(f, [f.Corners[on[0]], f.Corners[on[1]]], f.Corners[apex[0]]);
    }

    /// <summary>
    /// The two planes through the triangle's legs, perpendicular to it, as outward half-spaces <c>[nx, ny, nz, d]</c>:
    /// they bound the recess at its back (the vertical leg) and its top (the horizontal leg).
    /// </summary>
    public IEnumerable<double[]> LegPlanes()
    {
        for (var h = 0; h < 2; h++)
        {
            var from = Hypotenuse[h];
            var n = Vec.Unit(Vec.Cross(Facet.Normal, Vec.Sub(Apex, from)));
            if (Vec.Dot(n, Vec.Sub(Hypotenuse[1 - h], from)) > 0)
            {
                n = Vec.Scale(n, -1);
            }

            yield return [n[0], n[1], n[2], Vec.Dot(n, from)];
        }
    }
}

internal static class Vec
{
    public static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    public static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    public static double Length(double[] a) => Math.Sqrt(Dot(a, a));

    public static double[] Scale(double[] a, double s) => [a[0] * s, a[1] * s, a[2] * s];

    public static double[] Unit(double[] a) => Scale(a, 1 / Math.Max(Length(a), 1e-12));

    public static double[] Cross(double[] a, double[] b) =>
    [
        (a[1] * b[2]) - (a[2] * b[1]),
        (a[2] * b[0]) - (a[0] * b[2]),
        (a[0] * b[1]) - (a[1] * b[0]),
    ];
}
