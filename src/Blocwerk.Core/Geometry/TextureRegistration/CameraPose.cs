// <copyright file="CameraPose.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Registration;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// A pinhole camera recovered from one plane's homography (<see cref="FacetViewPrediction"/>): world → camera
/// <c>x = R·(X − o) + t</c> with <c>o</c> the anchor facet's origin, square pixels, principal point given.
/// </summary>
internal sealed class CameraPose
{
    private readonly double[] r;
    private readonly double[] t;
    private readonly double[] origin;
    private readonly double f;
    private readonly double cx;
    private readonly double cy;
    private readonly double[] centre;
    private readonly double anchorSide;

    private CameraPose(double[] rotation, double[] translation, double[] anchorOrigin, double focal, double px, double py, FacetFrame anchor)
    {
        (r, t, origin, f, cx, cy) = (rotation, translation, anchorOrigin, focal, px, py);
        centre = new double[3];
        for (var i = 0; i < 3; i++)
        {
            centre[i] = origin[i] - ((r[i] * t[0]) + (r[3 + i] * t[1]) + (r[6 + i] * t[2]));
        }

        anchorSide = Side(anchor);
    }

    /// <summary>The pose from a plane → photo px homography.</summary>
    /// <param name="g">Row-major plane (a, b) → photo px.</param>
    /// <param name="f">Focal length, px.</param>
    /// <param name="cx">Principal point x.</param>
    /// <param name="cy">Principal point y.</param>
    /// <param name="a">Plane a of a point known to be in view.</param>
    /// <param name="b">Plane b of that point.</param>
    /// <param name="frame">The plane's 3D frame.</param>
    /// <returns>The pose, or null when degenerate.</returns>
    public static CameraPose? FromHomography(double[] g, double f, double cx, double cy, double a, double b, FacetFrame frame)
    {
        // K⁻¹·G = λ·[r1 r2 t]
        var h = new double[9];
        for (var c = 0; c < 3; c++)
        {
            h[c] = (g[c] - (cx * g[6 + c])) / f;
            h[3 + c] = (g[3 + c] - (cy * g[6 + c])) / f;
            h[6 + c] = g[6 + c];
        }

        double[] h1 = [h[0], h[3], h[6]], h2 = [h[1], h[4], h[7]], h3 = [h[2], h[5], h[8]];
        var lambda = (Vec3.Length(h1) + Vec3.Length(h2)) / 2;
        if (lambda < 1e-12)
        {
            return null;
        }

        lambda *= (h[6] * a) + (h[7] * b) + h[8] < 0 ? -1 : 1;
        var r1 = Normalize(Vec3.Scale(h1, 1 / lambda));
        var r2raw = Vec3.Scale(h2, 1 / lambda);
        var r2 = Normalize(Vec3.Sub(r2raw, Vec3.Scale(r1, Vec3.Dot(r1, r2raw))));
        var r3 = Cross(r1, r2);
        var n = Normalize(Cross(frame.U, frame.V));
        var rot = new double[9];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                rot[(3 * i) + j] = (r1[i] * frame.U[j]) + (r2[i] * frame.V[j]) + (r3[i] * n[j]);
            }
        }

        return new CameraPose(rot, Vec3.Scale(h3, 1 / lambda), frame.Origin, f, cx, cy, frame);
    }

    /// <summary>The camera centre, world mm.</summary>
    public double[] Centre => (double[])centre.Clone();

    /// <summary>Whether the camera sees the facet from the same side as it sees the anchor facet.</summary>
    /// <param name="facet">The facet.</param>
    /// <returns>True when it faces the camera like the anchor does.</returns>
    public bool Faces(FacetFrame facet) => Side(facet) * anchorSide > 0;

    /// <summary>The facet's plane (a, b) → photo px homography.</summary>
    /// <param name="facet">The facet.</param>
    /// <returns>The mapping.</returns>
    public PlaneHomography PlaneToPhoto(FacetFrame facet)
    {
        var m = CameraMatrix(facet);
        var g = new double[9];
        for (var c = 0; c < 3; c++)
        {
            g[c] = (f * m[c]) + (cx * m[6 + c]);
            g[3 + c] = (f * m[3 + c]) + (cy * m[6 + c]);
            g[6 + c] = m[6 + c];
        }

        return PlaneHomography.FromCoefficients(g);
    }

    /// <summary>The camera-space depth of a plane point of the facet (positive in front of the camera).</summary>
    /// <param name="facet">The facet.</param>
    /// <param name="a">Plane a.</param>
    /// <param name="b">Plane b.</param>
    /// <returns>The depth, mm.</returns>
    public double Depth(FacetFrame facet, double a, double b)
    {
        var m = CameraMatrix(facet);
        return (m[6] * a) + (m[7] * b) + m[8];
    }

    /// <summary>[R·u | R·v | R·(o_facet − o) + t], row-major: plane (a, b, 1) → camera space.</summary>
    private double[] CameraMatrix(FacetFrame facet)
    {
        var ru = Rotate(facet.U);
        var rv = Rotate(facet.V);
        var ro = Vec3.Add(Rotate(Vec3.Sub(facet.Origin, origin)), t);
        return [ru[0], rv[0], ro[0], ru[1], rv[1], ro[1], ru[2], rv[2], ro[2]];
    }

    private double[] Rotate(double[] v) =>
    [
        (r[0] * v[0]) + (r[1] * v[1]) + (r[2] * v[2]),
        (r[3] * v[0]) + (r[4] * v[1]) + (r[5] * v[2]),
        (r[6] * v[0]) + (r[7] * v[1]) + (r[8] * v[2]),
    ];

    private double Side(FacetFrame facet) => Vec3.Dot(facet.Normal, Vec3.Sub(centre, facet.Origin));

    private static double[] Normalize(double[] v) => Vec3.Scale(v, 1 / Vec3.Length(v));

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];
}
