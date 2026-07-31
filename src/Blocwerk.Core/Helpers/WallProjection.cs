using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// Foreshortening maths for rendering a wall photo. Pure and dependency-free so the
/// service layer, the renderers and the tests all agree on one projection.
/// </summary>
public static class WallProjection
{
    /// <summary>
    /// Corrects a normalized Y coordinate for the inclination of the wall region it falls
    /// into. The segment whose polygon contains the point wins and is squashed around its
    /// own top edge; a point outside every segment falls back to the whole-wall angle.
    /// </summary>
    /// <param name="x">Normalized X of the point, 0..1.</param>
    /// <param name="y">Normalized Y of the point, 0..1.</param>
    /// <param name="segments">The wall's segments, possibly empty.</param>
    /// <param name="fallbackAngle">The wall's own angle in degrees, used outside all segments.</param>
    public static double ProjectY(double x, double y, IReadOnlyList<WallSegment>? segments, int fallbackAngle)
    {
        var segment = FindSegment(x, y, segments);
        if (segment == null)
        {
            return ProjectYFlat(y, fallbackAngle);
        }

        if (segment.Angle <= 0 || segment.Points.Count < 3)
        {
            return y;
        }

        var cos = Math.Cos(segment.Angle * Math.PI / 180.0);
        var yTop = segment.Points.Min(p => p.Dy);
        return yTop + ((y - yTop) * cos);
    }

    /// <summary>
    /// Builds the multi-panel schematic layout for a wall. This is the oriented-plane model that
    /// supersedes the Y-only <see cref="ProjectY"/>: it folds differently-oriented segments (a
    /// vertical side wall meeting an overhanging main wall) into a continuous 2D map. Build once
    /// per wall and reuse; see <see cref="WallSchematicLayout"/>.
    /// </summary>
    public static WallSchematicLayout BuildLayout(IReadOnlyList<WallSegment>? segments, int fallbackAngle) =>
        WallSchematicLayout.Build(segments, fallbackAngle);

    /// <summary>
    /// The single-plane projection: squash the whole image around its vertical midline.
    /// </summary>
    public static double ProjectYFlat(double y, int angle)
    {
        if (angle <= 0)
        {
            return y;
        }

        var cos = Math.Cos(angle * Math.PI / 180.0);
        return (y * cos) + ((1 - cos) * 0.5);
    }

    /// <summary>
    /// The segments that belong in the folded schematic, in <see cref="WallSegment.SortOrder"/>
    /// order. Floor panels are dropped (they are the ground, not the climbable wall), and — when
    /// <paramref name="holds"/> is non-empty — so is any wall panel that contains none of them, so
    /// the schematic shows only the parts of the wall that actually carry holds. Passing no holds
    /// keeps every non-floor segment, which is what a wall still being set up wants.
    /// </summary>
    public static List<WallSegment> SchematicSegments(
        IReadOnlyList<WallSegment>? segments, IReadOnlyList<Hold>? holds)
    {
        if (segments == null || segments.Count == 0)
        {
            return [];
        }

        var trimEmpty = holds is { Count: > 0 };
        return segments
            .Where(s => s.Kind != WallSegmentKind.Floor && s.Points.Count >= 3)
            .Where(s => !trimEmpty || holds!.Any(h => IsPointInPolygon(h.X, h.Y, s.Points)))
            .OrderBy(s => s.SortOrder)
            .ToList();
    }

    /// <summary>
    /// True when the point falls inside a <see cref="WallSegmentKind.Floor"/> segment, i.e. off
    /// the climbable wall. Such holds are not drawn in the schematic.
    /// </summary>
    public static bool IsOnFloor(double x, double y, IReadOnlyList<WallSegment>? segments) =>
        segments != null && segments.Any(s =>
            s.Kind == WallSegmentKind.Floor && IsPointInPolygon(x, y, s.Points));

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

        foreach (var segment in segments.OrderBy(s => s.SortOrder))
        {
            if (IsPointInPolygon(x, y, segment.Points))
            {
                return segment;
            }
        }

        return null;
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
