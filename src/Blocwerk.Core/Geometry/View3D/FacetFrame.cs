// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// A facet's plane frame from <c>wall-geometry.json</c>: plane point (a, b) sits at
/// <c>Origin + a·U + b·V</c> in world mm, and <see cref="Normal"/> points out of the wall toward the
/// climber (see <c>tools/glyph/wall-geometry.schema.md</c>).
/// </summary>
public sealed class FacetFrame
{
    private FacetFrame(double[] origin, double[] u, double[] v, double[] normal)
    {
        Origin = origin;
        U = u;
        V = v;
        Normal = normal;
    }

    public double[] Origin { get; }

    public double[] U { get; }

    public double[] V { get; }

    public double[] Normal { get; }

    /// <summary>
    /// The frame of <paramref name="facet"/>, or null when its origin/u/v are missing or degenerate.
    /// A missing normal is derived as <c>u × v</c>, the schema's definition.
    /// </summary>
    public static FacetFrame? From(WallGeometryFacet facet)
    {
        if (!IsVec3(facet.Origin) || !IsVec3(facet.U) || !IsVec3(facet.V))
        {
            return null;
        }

        var u = Normalize(facet.U!);
        var v = Normalize(facet.V!);
        if (u is null || v is null)
        {
            return null;
        }

        var normal = IsVec3(facet.Normal) ? Normalize(facet.Normal!) : null;
        normal ??= Normalize(Cross(u, v));
        return normal is null ? null : new FacetFrame([.. facet.Origin!], u, v, normal);
    }

    /// <summary>The world point of plane coordinates (a, b), lifted <paramref name="lift"/> mm along the normal.</summary>
    public double[] ToWorld(double a, double b, double lift = 0)
    {
        var p = new double[3];
        for (var i = 0; i < 3; i++)
        {
            p[i] = Origin[i] + (a * U[i]) + (b * V[i]) + (lift * Normal[i]);
        }

        return p;
    }

    /// <summary>The four corners of a plane rectangle: (aMin,bMin), (aMax,bMin), (aMax,bMax), (aMin,bMax).</summary>
    public IReadOnlyList<double[]> Corners(PlaneRectMm rect) =>
    [
        ToWorld(rect.AMin, rect.BMin),
        ToWorld(rect.AMax, rect.BMin),
        ToWorld(rect.AMax, rect.BMax),
        ToWorld(rect.AMin, rect.BMax),
    ];

    private static bool IsVec3(double[]? v) => v is { Length: 3 } && v.All(double.IsFinite);

    private static double[] Cross(double[] a, double[] b) =>
    [
        (a[1] * b[2]) - (a[2] * b[1]),
        (a[2] * b[0]) - (a[0] * b[2]),
        (a[0] * b[1]) - (a[1] * b[0]),
    ];

    private static double[]? Normalize(double[] v)
    {
        var len = Math.Sqrt((v[0] * v[0]) + (v[1] * v[1]) + (v[2] * v[2]));
        return len < 1e-9 ? null : [v[0] / len, v[1] / len, v[2] / len];
    }
}
