using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// The schematic projection for a whole wall, computed once and then queried per point.
/// <para>
/// Each segment is treated as a differently-oriented planar panel (see <see cref="PanelPlane"/>).
/// The panels are laid out by folding along their shared edges: a root panel is anchored exactly
/// where the legacy single-plane projection put it, then every adjacent panel is pinned onto its
/// neighbour's already-placed hinge edge so the map stays continuous. A point outside every
/// segment falls back to the whole-wall angle, identical to <see cref="WallProjection.ProjectYFlat"/>.
/// </para>
/// <para>
/// Building is O(segments^2) (tiny in practice) and projecting a point is O(segments); construct
/// one layout per wall and reuse it across a render loop rather than rebuilding per hold.
/// </para>
/// </summary>
public sealed class WallSchematicLayout
{
    private readonly List<PlacedPanel> panels;
    private readonly int fallbackAngle;

    private WallSchematicLayout(List<PlacedPanel> placedPanels, int wallFallbackAngle)
    {
        panels = placedPanels;
        fallbackAngle = wallFallbackAngle;
    }

    /// <summary>Number of segments the layout covers, in <see cref="WallSegment.SortOrder"/> order.</summary>
    public int SegmentCount => panels.Count;

    /// <summary>
    /// Builds the layout for a wall. <paramref name="segments"/> may be null or empty, in which
    /// case every point falls back to <paramref name="fallbackAngle"/> and the result is
    /// bit-identical to the old whole-wall projection.
    /// </summary>
    public static WallSchematicLayout Build(IReadOnlyList<WallSegment>? segments, int fallbackAngle)
    {
        var ordered = (segments ?? [])
            .Where(s => s.Points.Count >= 3)
            .OrderBy(s => s.SortOrder)
            .ToList();

        var placed = ordered
            .Select(s => new PlacedPanel
            {
                Points = s.Points,
                Plane = PanelPlane.FromDegrees(s.Angle, s.Yaw),
            })
            .ToList();

        SolveLayout(ordered, placed);
        return new WallSchematicLayout(placed, fallbackAngle);
    }

    /// <summary>
    /// Projects a photo-space point into the schematic. The segment whose polygon contains the
    /// point wins (first in sort order on overlap); a point in no segment uses the wall angle.
    /// </summary>
    public SchematicPoint Project(double x, double y)
    {
        for (var i = 0; i < panels.Count; i++)
        {
            if (WallProjection.IsPointInPolygon(x, y, panels[i].Points))
            {
                return panels[i].Project(x, y);
            }
        }

        return new SchematicPoint(x, WallProjection.ProjectYFlat(y, fallbackAngle));
    }

    /// <summary>
    /// Y-only shim over <see cref="Project"/> for call sites that still consume a corrected Y.
    /// Loses the horizontal correction a yawed panel introduces; prefer <see cref="Project"/>.
    /// </summary>
    public double ProjectY(double x, double y) => Project(x, y).Y;

    /// <summary>
    /// Projects a point using a specific segment's transform, skipping the containment test.
    /// Use this to draw a segment's own polygon, whose vertices sit on the boundary where
    /// containment is ambiguous. <paramref name="index"/> is the sort-order index.
    /// </summary>
    public SchematicPoint ProjectForSegment(int index, double x, double y)
    {
        if (index < 0 || index >= panels.Count)
        {
            return new SchematicPoint(x, y);
        }

        return panels[index].Project(x, y);
    }

    /// <summary>
    /// Projects the outline of segment <paramref name="index"/> for drawing it in the schematic.
    /// </summary>
    public IReadOnlyList<SchematicPoint> ProjectSegmentPolygon(int index)
    {
        if (index < 0 || index >= panels.Count)
        {
            return [];
        }

        return panels[index].Points.Select(p => panels[index].Project(p)).ToList();
    }

    /// <summary>
    /// Projects an arbitrary polygon vertex-by-vertex through <see cref="Project"/>, so the
    /// result is exactly the individually-projected vertices in order.
    /// </summary>
    public IReadOnlyList<SchematicPoint> ProjectPolygon(IEnumerable<ShapePoint> points) =>
        points.Select(p => Project(p.Dx, p.Dy)).ToList();

    private static void SolveLayout(List<WallSegment> ordered, List<PlacedPanel> placed)
    {
        if (placed.Count == 0)
        {
            return;
        }

        // Anchor the first panel exactly where the legacy per-segment projection sat it, then
        // grow the placed set by folding each unplaced neighbour onto a placed hinge edge.
        AnchorAtTop(placed[0]);
        placed[0].Placed = true;

        var progressed = true;
        while (progressed)
        {
            progressed = false;
            for (var child = 0; child < placed.Count; child++)
            {
                if (placed[child].Placed)
                {
                    continue;
                }

                if (TryFoldOntoNeighbour(ordered, placed, child))
                {
                    placed[child].Placed = true;
                    progressed = true;
                }
            }
        }

        // Panels that never touch a placed one (islands / no shared edge) are self-anchored so
        // they still render sensibly at their own photo location instead of collapsing to origin.
        foreach (var panel in placed.Where(p => !p.Placed))
        {
            AnchorAtTop(panel);
            panel.Placed = true;
        }
    }

    private static bool TryFoldOntoNeighbour(List<WallSegment> ordered, List<PlacedPanel> placed, int child)
    {
        for (var parent = 0; parent < placed.Count; parent++)
        {
            if (!placed[parent].Placed)
            {
                continue;
            }

            var edge = SegmentAdjacency.SharedEdge(ordered[parent].Points, ordered[child].Points);
            if (edge == null)
            {
                continue;
            }

            var (e0, e1) = edge.Value;
            var p0 = placed[parent].Project(e0);
            var p1 = placed[parent].Project(e1);
            var c0 = placed[child].Plane.Apply(e0.Dx, e0.Dy);
            var c1 = placed[child].Plane.Apply(e1.Dx, e1.Dy);
            placed[child].Placement = SimilarityTransform.FromEdgeMatch(c0, c1, p0, p1);
            return true;
        }

        return false;
    }

    private static void AnchorAtTop(PlacedPanel panel)
    {
        var top = panel.Points[0];
        foreach (var p in panel.Points)
        {
            if (p.Dy < top.Dy || (p.Dy == top.Dy && p.Dx < top.Dx))
            {
                top = p;
            }
        }

        var raw = panel.Plane.Apply(top.Dx, top.Dy);
        panel.Placement = SimilarityTransform.Translation(top.Dx - raw.X, top.Dy - raw.Y);
    }
}
