using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// A wall segment while it is being edited. The editor submits the whole set through
/// <see cref="IWallSegmentService.ReplaceSegmentsAsync"/>, so a draft carries no database
/// identity of its own; <see cref="Key"/> exists purely to keep Blazor's diffing stable
/// while rows are reordered.
/// </summary>
public class WallSegmentDraft
{
    public Guid Key { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "Segment";

    /// <summary>Inclination in degrees, <see cref="WallAngleRange"/>.</summary>
    public int Angle { get; set; }

    /// <summary>
    /// Yaw in degrees (-90..90): how far the panel is turned to face sideways instead of the
    /// camera. 0 faces the camera (legacy behaviour); positive turns the right edge away (a
    /// side wall on the right), negative the left. Surfaced in the UI as a "facing" toggle.
    /// </summary>
    public int Yaw { get; set; }

    /// <summary>Absolute normalized (0..1) polygon vertices.</summary>
    public List<ShapePoint> Points { get; set; } = [];

    public static WallSegmentDraft FromEntity(WallSegment segment) => new()
    {
        Key = segment.Id,
        Name = segment.Name,
        Angle = segment.Angle,
        Yaw = segment.Yaw,
        Points = segment.Points.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList(),
    };

    public WallSegmentInput ToInput(int sortOrder) =>
        new(Name, Angle, Points.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList(), sortOrder, Yaw);

    /// <summary>
    /// A throwaway entity so the live preview can run the very same
    /// <c>WallProjection</c> maths the server and the renderers use.
    /// </summary>
    public WallSegment ToEntity(int sortOrder) => new()
    {
        Id = Key,
        Name = Name,
        Angle = Angle,
        Yaw = Yaw,
        Points = Points,
        SortOrder = sortOrder,
    };

    /// <summary>
    /// A regular n-gon centred on the given point. New segments start as a hexagon
    /// because six edge points cover the usual slab / vertical / overhang panel, but the
    /// count is not fixed — vertices can be added and removed afterwards.
    /// </summary>
    public static List<ShapePoint> Polygon(double cx, double cy, double rx, double ry, int corners = 6)
    {
        var points = new List<ShapePoint>(corners);
        for (var i = 0; i < corners; i++)
        {
            // Start at the top so a hexagon reads as a panel with a flat-ish top edge.
            var angle = (i * 2 * Math.PI / corners) - (Math.PI / 2);
            points.Add(new ShapePoint
            {
                Dx = Math.Round(Math.Clamp(cx + (Math.Cos(angle) * rx), 0, 1), 5),
                Dy = Math.Round(Math.Clamp(cy + (Math.Sin(angle) * ry), 0, 1), 5),
            });
        }

        return points;
    }

    /// <summary>Splits the longest edge, so the polygon gains detail where it is coarsest.</summary>
    public void AddVertexOnLongestEdge()
    {
        var edge = 0;
        var longest = -1.0;
        for (var i = 0; i < Points.Count; i++)
        {
            var a = Points[i];
            var b = Points[(i + 1) % Points.Count];
            var lenSq = ((b.Dx - a.Dx) * (b.Dx - a.Dx)) + ((b.Dy - a.Dy) * (b.Dy - a.Dy));
            if (lenSq > longest)
            {
                longest = lenSq;
                edge = i;
            }
        }

        var p1 = Points[edge];
        var p2 = Points[(edge + 1) % Points.Count];
        Points.Insert(edge + 1, new ShapePoint { Dx = (p1.Dx + p2.Dx) / 2, Dy = (p1.Dy + p2.Dy) / 2 });
    }

    /// <summary>
    /// Slides the whole polygon by (dx, dy), clamped once for the entire shape so dragging
    /// into a wall edge slides along it instead of squashing the polygon flat against it.
    /// </summary>
    public void Translate(double dx, double dy)
    {
        dx = Math.Clamp(dx, -Points.Min(p => p.Dx), 1 - Points.Max(p => p.Dx));
        dy = Math.Clamp(dy, -Points.Min(p => p.Dy), 1 - Points.Max(p => p.Dy));
        foreach (var p in Points)
        {
            p.Dx += dx;
            p.Dy += dy;
        }
    }
}
