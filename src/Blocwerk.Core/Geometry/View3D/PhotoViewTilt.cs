// <copyright file="PhotoViewTilt.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// How obliquely a photo looked at a facet, read off its photo → facet homography. For a homography
/// the area magnification is <c>det(H) / w³</c>, with <c>w</c> the projective denominator, so
/// <c>|∇ ln |det J|| / 3 = |∇w| / |w|</c>: zero for a camera square-on to the facet, growing as the
/// camera tilts away. Measured per normalised photo unit, so two photos of one camera compare directly.
/// </summary>
public static class PhotoViewTilt
{
    /// <summary>Step of the inner finite difference (the Jacobian), normalised photo units.</summary>
    private const double JacobianStep = 0.002;

    /// <summary>Half-span over which the change of the Jacobian is measured, normalised photo units.</summary>
    private const double Span = 0.05;

    /// <summary>The tilt of <paramref name="map"/> at photo point (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <param name="map">The photo → facet mapping.</param>
    /// <param name="x">Normalised photo x.</param>
    /// <param name="y">Normalised photo y.</param>
    /// <returns>The tilt (≥ 0), or null when the mapping is not usable there.</returns>
    public static double? At(PhotoToPlane map, double x, double y)
    {
        var right = LogArea(map, x + Span, y);
        var left = LogArea(map, x - Span, y);
        var down = LogArea(map, x, y + Span);
        var up = LogArea(map, x, y - Span);
        if (right is not { } r || left is not { } l || down is not { } d || up is not { } u)
        {
            return null;
        }

        var gx = (r - l) / (2 * Span);
        var gy = (d - u) / (2 * Span);
        return Math.Sqrt((gx * gx) + (gy * gy)) / 3;
    }

    /// <summary>ln |det J| of the mapping at a point, or null when it is not finite or degenerate.</summary>
    private static double? LogArea(PhotoToPlane map, double x, double y)
    {
        var (ax1, bx1) = map(x + JacobianStep, y);
        var (ax0, bx0) = map(x - JacobianStep, y);
        var (ay1, by1) = map(x, y + JacobianStep);
        var (ay0, by0) = map(x, y - JacobianStep);
        var det = (((ax1 - ax0) * (by1 - by0)) - ((ay1 - ay0) * (bx1 - bx0))) / (4 * JacobianStep * JacobianStep);
        return double.IsFinite(det) && Math.Abs(det) > 1e-12 ? Math.Log(Math.Abs(det)) : null;
    }
}
