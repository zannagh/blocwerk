// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Which side of its seam a triangle facet keeps (<see cref="Wall3DFacetOutlines"/>). First the plan's right-angle
/// corner: a plan segment's (x, y) frame is the solved facet's (a, b) — both are "right as you face the markers, up the
/// surface" (the solver's u = up × n), so yaw never mirrors it — and the kept half is the one holding that corner of the
/// extent, when the seam clearly separates it from the diagonally opposite corner (the plan's triangle fits this cut).
/// Else the side behind the cutting facet (against its climber-facing normal), else the markers' centroid's side.
/// </summary>
public static class TriangleKeptSide
{
    /// <summary>The right-angle corner and its opposite must each lie this share of the extent's diagonal off the seam.</summary>
    public const double MinCornerShare = 0.1;

    /// <summary>A facet's b must rise at least this much (per mm) for "bottom" and "top" to mean the plan's.</summary>
    public const double MinUpComponent = 0.2;

    /// <summary>Without a facing normal, a marker centroid this close to the seam does not say which side the facet is on.</summary>
    public const double MinCentroidDistanceMm = 10;

    /// <summary>
    /// +1 to keep where <paramref name="side"/> ≥ 0, −1 for the other half; null when nothing says.
    /// </summary>
    public static int? Sign(
        Func<double, double, double> side,
        PlaneRectMm extent,
        TriangleCorner? rightAngle,
        double[]? otherNormal,
        (double A, double B)? centroid)
    {
        if (rightAngle is { } corner && FromCorner(side, extent, corner) is { } fromPlan)
        {
            return fromPlan;
        }

        if (Wall3DFacetOutlines.IsUnit(otherNormal))
        {
            return -1;
        }

        var keep = centroid is { } c ? side(c.A, c.B) : 0;
        return Math.Abs(keep) < MinCentroidDistanceMm ? null : Math.Sign(keep);
    }

    /// <summary><paramref name="rightAngle"/> when the facet's b runs up its surface (so the plan's corner names one of its corners), else null.</summary>
    public static TriangleCorner? Mapped(Wall3DFacet facet, double[]? up, TriangleCorner rightAngle)
    {
        var u = up is { Length: 3 } ? up : [0, 0, 1];
        return Wall3DFacetOutlines.Dot(facet.V, u) >= MinUpComponent ? rightAngle : null;
    }

    private static int? FromCorner(Func<double, double, double> side, PlaneRectMm e, TriangleCorner corner)
    {
        var (c, d) = Corners(e, corner);
        var sc = side(c[0], c[1]);
        var sd = side(d[0], d[1]);
        var min = MinCornerShare * Math.Sqrt(Math.Pow(e.AMax - e.AMin, 2) + Math.Pow(e.BMax - e.BMin, 2));
        return sc * sd < 0 && Math.Abs(sc) >= min && Math.Abs(sd) >= min ? Math.Sign(sc) : null;
    }

    private static (double[] Corner, double[] Opposite) Corners(PlaneRectMm e, TriangleCorner corner) => corner switch
    {
        TriangleCorner.BottomLeft => ([e.AMin, e.BMin], [e.AMax, e.BMax]),
        TriangleCorner.BottomRight => ([e.AMax, e.BMin], [e.AMin, e.BMax]),
        TriangleCorner.TopRight => ([e.AMax, e.BMax], [e.AMin, e.BMin]),
        _ => ([e.AMin, e.BMax], [e.AMax, e.BMin]),
    };
}
