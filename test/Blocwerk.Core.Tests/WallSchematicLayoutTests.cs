using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Locks down the oriented-plane schematic: differently-facing panels (a vertical side wall
/// meeting an overhanging main wall) fold along their shared edges into a continuous 2D map,
/// while a wall with no segments — or only default-orientation segments — projects exactly as
/// the legacy single-plane maths did.
/// <para>
/// Angle convention: <see cref="WallSegment.Angle"/> is inclination from vertical, so the user's
/// "angle against the ground" G maps to inclination = 90 - G. Their 90° (a wall perpendicular to
/// the ground) is inclination 0; 60° is inclination 30; 45° is inclination 45.
/// </para>
/// </summary>
public class WallSchematicLayoutTests
{
    private const double Exact = 1e-9;
    private const double Tol = 1e-6;

    [Fact]
    public void Project_NoSegments_MatchesProjectYFlatExactly()
    {
        var layout = WallSchematicLayout.Build([], fallbackAngle: 45);

        foreach (var y in new[] { 0.0, 0.2, 0.5, 0.8, 1.0 })
        {
            var p = layout.Project(0.37, y);
            Assert.Equal(0.37, p.X, Exact);
            Assert.Equal(WallProjection.ProjectYFlat(y, 45), p.Y, Exact);
        }

        // Null segments behave like an empty set.
        Assert.Equal(WallProjection.ProjectYFlat(0.6, 30), WallSchematicLayout.Build(null, 30).Project(0.5, 0.6).Y, Exact);
    }

    [Fact]
    public void Project_SingleDefaultSegment_MatchesLegacyProjectY()
    {
        var segments = new[] { Segment("Overhang", angle: 30, y0: 0.5, y1: 1.0) };
        var layout = WallSchematicLayout.Build(segments, fallbackAngle: 60);

        // Inside the segment: identical corrected Y to the legacy per-segment formula, X untouched.
        var inside = layout.Project(0.42, 0.8);
        Assert.Equal(0.42, inside.X, Exact);
        Assert.Equal(WallProjection.ProjectY(0.42, 0.8, segments, 60), inside.Y, Exact);

        // Outside the segment: falls back to the whole-wall angle, exactly like before.
        var outside = layout.Project(0.42, 0.2);
        Assert.Equal(WallProjection.ProjectYFlat(0.2, 60), outside.Y, Exact);
        Assert.Equal(WallProjection.ProjectY(0.42, 0.2, segments, 60), outside.Y, Exact);
    }

    [Fact]
    public void Project_ZeroAngleDefaultSegment_IsIdentity()
    {
        var layout = WallSchematicLayout.Build([Segment("Vertical", angle: 0, y0: 0.0, y1: 1.0)], fallbackAngle: 0);

        var p = layout.Project(0.3, 0.7);
        Assert.Equal(0.3, p.X, Exact);
        Assert.Equal(0.7, p.Y, Exact);
    }

    [Fact]
    public void UsersWall_VerticalSidePanel_IsNotSquashedAsIfCoplanar_AndHingeStaysCoincident()
    {
        // Main wall overhangs at 45° (inclination 45); a vertical side panel (inclination 0) is
        // turned to face sideways (yaw 70) and shares the main wall's right edge at x = 0.5.
        var main = Segment("Main", angle: 45, y0: 0.2, y1: 1.0, x0: 0.0, x1: 0.5, sortOrder: 0);
        var side = Segment("Side", angle: 0, y0: 0.2, y1: 1.0, x0: 0.5, x1: 0.8, sortOrder: 1, yaw: 70);
        var layout = WallSchematicLayout.Build([main, side], fallbackAngle: 0);

        // The hinge edge is drawn identically from both panels: no tear.
        AssertClose(layout.ProjectForSegment(0, 0.5, 0.2), layout.ProjectForSegment(1, 0.5, 0.2));
        AssertClose(layout.ProjectForSegment(0, 0.5, 1.0), layout.ProjectForSegment(1, 0.5, 1.0));

        // A horizontal step across the side panel foreshortens; if it were treated as part of the
        // main plane (yaw 0) it would keep its full width instead.
        var a = layout.ProjectForSegment(1, 0.6, 0.5);
        var b = layout.ProjectForSegment(1, 0.8, 0.5);
        Assert.True(Math.Abs(b.X - a.X) < 0.2 - 1e-3, "side panel horizontal extent should foreshorten");

        // Modelling the same panel as a coplanar overhang (inclination 45, yaw 0) lands an interior
        // hold in a visibly different place, proving the side wall is not flattened into the main plane.
        var coplanar = Segment("Side", angle: 45, y0: 0.2, y1: 1.0, x0: 0.5, x1: 0.8, sortOrder: 1, yaw: 0);
        var coplanarLayout = WallSchematicLayout.Build([main, coplanar], fallbackAngle: 0);
        var oriented = layout.Project(0.7, 0.5);
        var flattened = coplanarLayout.Project(0.7, 0.5);
        Assert.True(Math.Abs(oriented.X - flattened.X) > 0.1, "oriented side wall must differ from the coplanar treatment");
    }

    [Fact]
    public void ThreePanels_90_60_45_FoldContinuouslyAtEveryHinge()
    {
        // User wording 90 / 60 / 45 against the ground -> inclinations 0 / 30 / 45, stacked.
        var top = Segment("Vertical", angle: 0, y0: 0.0, y1: 0.34, sortOrder: 0);
        var mid = Segment("Steep", angle: 30, y0: 0.34, y1: 0.67, sortOrder: 1);
        var low = Segment("Overhang", angle: 45, y0: 0.67, y1: 1.0, sortOrder: 2);
        var layout = WallSchematicLayout.Build([top, mid, low], fallbackAngle: 20);

        // Each shared edge is drawn to the same place by both neighbours.
        AssertClose(layout.ProjectForSegment(0, 0.5, 0.34), layout.ProjectForSegment(1, 0.5, 0.34));
        AssertClose(layout.ProjectForSegment(0, 1.0, 0.34), layout.ProjectForSegment(1, 1.0, 0.34));
        AssertClose(layout.ProjectForSegment(1, 0.5, 0.67), layout.ProjectForSegment(2, 0.5, 0.67));
        AssertClose(layout.ProjectForSegment(1, 1.0, 0.67), layout.ProjectForSegment(2, 1.0, 0.67));
    }

    [Fact]
    public void Project_OverlappingRegion_PicksLowestSortOrder()
    {
        var first = Segment("First", angle: 10, y0: 0.0, y1: 1.0, sortOrder: 0);
        var second = Segment("Second", angle: 40, y0: 0.0, y1: 1.0, sortOrder: 1);

        // Deliberately hand them in the wrong order to prove sort order, not list order, wins.
        var layout = WallSchematicLayout.Build([second, first], fallbackAngle: 0);
        AssertClose(layout.Project(0.5, 0.5), layout.ProjectForSegment(0, 0.5, 0.5));
    }

    [Fact]
    public void Project_OutsideEverySegment_UsesFallback()
    {
        var layout = WallSchematicLayout.Build([Segment("Lower", angle: 30, y0: 0.6, y1: 1.0)], fallbackAngle: 45);

        var p = layout.Project(0.5, 0.2);
        Assert.Equal(0.5, p.X, Exact);
        Assert.Equal(WallProjection.ProjectYFlat(0.2, 45), p.Y, Exact);
    }

    [Fact]
    public void Project_OnSharedEdge_IsUnambiguousBecauseBothPanelsAgree()
    {
        var top = Segment("Top", angle: 0, y0: 0.0, y1: 0.5, sortOrder: 0);
        var bottom = Segment("Bottom", angle: 40, y0: 0.5, y1: 1.0, sortOrder: 1);
        var layout = WallSchematicLayout.Build([top, bottom], fallbackAngle: 0);

        var fromTop = layout.ProjectForSegment(0, 0.5, 0.5);
        var fromBottom = layout.ProjectForSegment(1, 0.5, 0.5);
        AssertClose(fromTop, fromBottom);

        // Whichever panel containment assigns the edge point to, the answer is that shared value.
        var actual = layout.Project(0.5, 0.5);
        Assert.True(
            Distance(actual, fromTop) < Tol || Distance(actual, fromBottom) < Tol,
            "a hold on the hinge must land on the shared edge");
    }

    [Fact]
    public void AbuttingPanels_WithNonCoincidentVertices_StillFoldContinuously()
    {
        // The real-world case: a 0° kickboard facing the camera and a 45° main wall above it, drawn
        // as independent polygons. Their shared boundary is the line y = 0.6, but the two panels put
        // their vertices at different x along it, so no vertex coincides — the fold must still fire.
        var kickboard = new WallSegment
        {
            Name = "Kickboard",
            Angle = 0,
            SortOrder = 0,
            Points =
            [
                new ShapePoint { Dx = 0.05, Dy = 0.6 },
                new ShapePoint { Dx = 0.55, Dy = 0.6 },
                new ShapePoint { Dx = 0.95, Dy = 0.6 },
                new ShapePoint { Dx = 0.95, Dy = 1.0 },
                new ShapePoint { Dx = 0.05, Dy = 1.0 },
            ],
        };
        var main = new WallSegment
        {
            Name = "Main",
            Angle = 45,
            SortOrder = 1,
            Points =
            [
                new ShapePoint { Dx = 0.0, Dy = 0.1 },
                new ShapePoint { Dx = 1.0, Dy = 0.1 },
                new ShapePoint { Dx = 0.7, Dy = 0.6 },
                new ShapePoint { Dx = 0.3, Dy = 0.6 },
            ],
        };

        var layout = WallSchematicLayout.Build([kickboard, main], fallbackAngle: 0);

        // A point on the shared line lands in the same schematic place whether the kickboard panel
        // (index 0) or the folded main panel (index 1) draws it: the panels meet, they don't drift.
        AssertClose(
            layout.ProjectForSegment(0, 0.5, 0.6),
            layout.ProjectForSegment(1, 0.5, 0.6),
            0.02);
    }

    [Fact]
    public void OrphanPanel_WithNoSharedEdge_StillProjectsAtItsOwnLocation()
    {
        var mainLeft = Segment("Left", angle: 20, y0: 0.0, y1: 1.0, x0: 0.0, x1: 0.3, sortOrder: 0);
        var island = Segment("Island", angle: 30, y0: 0.4, y1: 0.6, x0: 0.6, x1: 0.9, sortOrder: 1);
        var layout = WallSchematicLayout.Build([mainLeft, island], fallbackAngle: 0);

        // Self-anchored at its own top edge (min Dy = 0.4), so its top row does not move.
        var p = layout.ProjectForSegment(1, 0.6, 0.4);
        Assert.Equal(0.6, p.X, Tol);
        Assert.Equal(0.4, p.Y, Tol);
    }

    [Fact]
    public void Layout_IsDeterministic_AndPolygonMatchesItsVertices()
    {
        var segments = new[]
        {
            Segment("Main", angle: 45, y0: 0.2, y1: 1.0, x0: 0.0, x1: 0.5, sortOrder: 0),
            Segment("Side", angle: 0, y0: 0.2, y1: 1.0, x0: 0.5, x1: 0.8, sortOrder: 1, yaw: 70),
        };

        var a = WallSchematicLayout.Build(segments, 0);
        var b = WallSchematicLayout.Build(segments, 0);
        AssertClose(a.Project(0.7, 0.5), b.Project(0.7, 0.5), Exact);

        // Projecting a polygon is exactly its vertices projected one by one.
        var poly = new List<ShapePoint>
        {
            new() { Dx = 0.1, Dy = 0.3 },
            new() { Dx = 0.4, Dy = 0.4 },
            new() { Dx = 0.2, Dy = 0.9 },
        };
        var projected = a.ProjectPolygon(poly);
        for (var i = 0; i < poly.Count; i++)
        {
            AssertClose(projected[i], a.Project(poly[i].Dx, poly[i].Dy), Exact);
        }

        // The segment-polygon helper uses that segment's own transform for every vertex.
        var outline = a.ProjectSegmentPolygon(1);
        var sidePoints = segments[1].Points;
        for (var i = 0; i < sidePoints.Count; i++)
        {
            AssertClose(outline[i], a.ProjectForSegment(1, sidePoints[i].Dx, sidePoints[i].Dy), Exact);
        }
    }

    [Fact]
    public void ProjectY_Shim_MatchesTheYOfProject()
    {
        var segments = new[] { Segment("Overhang", angle: 35, y0: 0.4, y1: 1.0) };
        var layout = WallSchematicLayout.Build(segments, fallbackAngle: 50);

        Assert.Equal(layout.Project(0.5, 0.7).Y, layout.ProjectY(0.5, 0.7), Exact);
        Assert.Equal(layout.Project(0.5, 0.1).Y, layout.ProjectY(0.5, 0.1), Exact);
    }

    private static double Distance(SchematicPoint a, SchematicPoint b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static void AssertClose(SchematicPoint a, SchematicPoint b, double tol = Tol)
    {
        Assert.Equal(a.X, b.X, tol);
        Assert.Equal(a.Y, b.Y, tol);
    }

    private static WallSegment Segment(
        string name,
        int angle,
        double y0,
        double y1,
        double x0 = 0.0,
        double x1 = 1.0,
        int sortOrder = 0,
        int yaw = 0) =>
        new()
        {
            Name = name,
            Angle = angle,
            Yaw = yaw,
            SortOrder = sortOrder,
            Points =
            [
                new ShapePoint { Dx = x0, Dy = y0 },
                new ShapePoint { Dx = x1, Dy = y0 },
                new ShapePoint { Dx = x1, Dy = y1 },
                new ShapePoint { Dx = x0, Dy = y1 },
            ],
        };
}
