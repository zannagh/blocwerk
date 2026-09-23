// <copyright file="PlannerGeometry.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Web.Components.Shared.MarkerPlanner;

/// <summary>
/// Conversions between the net drawing frame (mm, y up) and a segment's own frame, for turning a click
/// or a drag on the canvas back into a marker position. A <see cref="NetSegment"/> maps its frame into
/// the net as "rotate counter-clockwise by <see cref="NetSegment.RotationDeg"/>, then translate by the
/// origin", so the inverse is "subtract the origin, then rotate back".
/// </summary>
public static class PlannerGeometry
{
    /// <summary>Grid the editor snaps marker positions to, in mm.</summary>
    public const double SnapMm = 5;

    /// <summary>A net-space point in <paramref name="segment"/>'s own frame (not snapped).</summary>
    public static PlanVector NetToSegment(NetSegment segment, double netX, double netY)
    {
        var relative = new PlanVector(netX - segment.OriginX, netY - segment.OriginY);
        return relative.Rotate(-segment.RotationDeg * Math.PI / 180.0);
    }

    /// <summary>A point of <paramref name="segment"/>'s own frame in net space.</summary>
    public static PlanVector SegmentToNet(NetSegment segment, double x, double y)
    {
        var rotated = new PlanVector(x, y).Rotate(segment.RotationDeg * Math.PI / 180.0);
        return new PlanVector(rotated.X + segment.OriginX, rotated.Y + segment.OriginY);
    }

    /// <summary>Rounds each coordinate to the nearest multiple of <paramref name="step"/>.</summary>
    public static PlanVector Snap(PlanVector point, double step = SnapMm) =>
        new(Math.Round(point.X / step) * step, Math.Round(point.Y / step) * step);

    /// <summary>The centre of a polygon's vertices (the marker square's centre, a label anchor).</summary>
    public static PlanVector Centre(IReadOnlyList<double[]> polygon)
    {
        if (polygon.Count == 0)
        {
            return default;
        }

        return new PlanVector(polygon.Average(p => p[0]), polygon.Average(p => p[1]));
    }

    /// <summary>True when the point lies inside the polygon (even-odd rule; edges count as outside).</summary>
    public static bool Contains(IReadOnlyList<double[]> polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (xi, yi) = (polygon[i][0], polygon[i][1]);
            var (xj, yj) = (polygon[j][0], polygon[j][1]);
            if ((yi > y) != (yj > y) && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>The segment whose outline contains the net point, or null.</summary>
    public static NetSegment? SegmentAt(NetGeometry net, double netX, double netY) =>
        net.Segments.FirstOrDefault(s => Contains(s.PolygonMm, netX, netY));
}
