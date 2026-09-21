using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// Foreshortening maths for rendering a wall photo. Pure and dependency-free so the
/// service layer, the renderers and the tests all agree on one projection.
/// </summary>
public static class WallProjection
{
    /// <summary>
    /// The first segment (in <see cref="WallSegment.SortOrder"/> order) whose polygon
    /// contains the point, or null when the point lies outside all of them.
    /// </summary>
    public static WallSegment? FindSegment(double x, double y, IReadOnlyList<WallSegment>? segments)
    {
        if (segments == null || segments.Count == 0)
        {
            return null;
        }

        return segments
            .OrderBy(s => s.SortOrder)
            .FirstOrDefault(s => IsPointInPolygon(x, y, s.Points));
    }

    /// <summary>
    /// True when the point lies inside any of the segments' polygons (their union).
    /// </summary>
    public static bool IsInsideAnySegment(double x, double y, IReadOnlyList<WallSegment>? segments) =>
        FindSegment(x, y, segments) != null;

    public static bool IsPointInPolygon(double px, double py, IReadOnlyList<ShapePoint>? polygon)
    {
        if (polygon == null || polygon.Count < 3)
        {
            return false;
        }

        var inside = false;
        var j = polygon.Count - 1;
        for (var i = 0; i < polygon.Count; i++)
        {
            if ((polygon[i].Dy > py) != (polygon[j].Dy > py) &&
                px < (((polygon[j].Dx - polygon[i].Dx) * (py - polygon[i].Dy) / (polygon[j].Dy - polygon[i].Dy)) + polygon[i].Dx))
            {
                inside = !inside;
            }

            j = i;
        }

        return inside;
    }

    public static bool IsPointInPolygon(double px, double py, IReadOnlyList<(double X, double Y)>? polygon)
    {
        if (polygon == null || polygon.Count < 3)
        {
            return false;
        }

        var inside = false;
        var j = polygon.Count - 1;
        for (var i = 0; i < polygon.Count; i++)
        {
            if ((polygon[i].Y > py) != (polygon[j].Y > py) &&
                px < (((polygon[j].X - polygon[i].X) * (py - polygon[i].Y) / (polygon[j].Y - polygon[i].Y)) + polygon[i].X))
            {
                inside = !inside;
            }

            j = i;
        }

        return inside;
    }
}
