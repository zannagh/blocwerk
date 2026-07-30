using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Locks down the foreshortening maths, which every wall renderer shares: a segment is
/// squashed around its own top edge, not around the middle of the photo.
/// </summary>
public class WallProjectionTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void ProjectY_IsIdentity_ForZeroAngle()
    {
        var segments = new[] { Segment("Vertical", 0, 0.0, 1.0) };

        Assert.Equal(0.4, WallProjection.ProjectY(0.5, 0.4, segments, fallbackAngle: 0), Tolerance);
        Assert.Equal(0.4, WallProjection.ProjectY(0.5, 0.4, [], fallbackAngle: 0), Tolerance);
    }

    [Fact]
    public void ProjectY_AnchorsAtTheSegmentTopEdge()
    {
        // Segment covering the lower half of the wall, so its top edge is at 0.5.
        var segments = new[] { Segment("Overhang", 30, 0.5, 1.0) };
        var cos = Math.Cos(30 * Math.PI / 180.0);

        // The top edge itself never moves.
        Assert.Equal(0.5, WallProjection.ProjectY(0.5, 0.5, segments, fallbackAngle: 0), Tolerance);

        // A point inside is pulled towards that edge, not towards the global midline.
        Assert.Equal(0.5 + (0.3 * cos), WallProjection.ProjectY(0.5, 0.8, segments, fallbackAngle: 0), Tolerance);
    }

    [Fact]
    public void ProjectY_UsesFallback_WhenNoSegmentContainsThePoint()
    {
        var segments = new[] { Segment("Lower", 30, 0.5, 1.0) };

        // y = 0.2 sits above the only segment.
        var expected = WallProjection.ProjectYFlat(0.2, 45);
        Assert.Equal(expected, WallProjection.ProjectY(0.5, 0.2, segments, fallbackAngle: 45), Tolerance);

        // And that fallback really is the old global formula.
        var cos = Math.Cos(45 * Math.PI / 180.0);
        Assert.Equal((0.2 * cos) + ((1 - cos) * 0.5), expected, Tolerance);
    }

    [Fact]
    public void ProjectY_ProjectsEachSegmentWithItsOwnAngle()
    {
        var slab = Segment("Slab", 10, 0.0, 0.5);
        var overhang = Segment("Overhang", 40, 0.5, 1.0);
        var segments = new[] { slab, overhang };

        var slabCos = Math.Cos(10 * Math.PI / 180.0);
        var overhangCos = Math.Cos(40 * Math.PI / 180.0);

        Assert.Equal(0.0 + (0.25 * slabCos), WallProjection.ProjectY(0.5, 0.25, segments, fallbackAngle: 60), Tolerance);
        Assert.Equal(0.5 + (0.25 * overhangCos), WallProjection.ProjectY(0.5, 0.75, segments, fallbackAngle: 60), Tolerance);

        // The two regions really do land in different places for the same offset.
        Assert.NotEqual(
            WallProjection.ProjectY(0.5, 0.25, segments, 60) - 0.0,
            WallProjection.ProjectY(0.5, 0.75, segments, 60) - 0.5,
            Tolerance);
    }

    [Fact]
    public void FindSegment_PicksBySortOrder_ForOverlappingPolygons()
    {
        var first = Segment("First", 10, 0.0, 1.0, sortOrder: 0);
        var second = Segment("Second", 40, 0.0, 1.0, sortOrder: 1);

        var found = WallProjection.FindSegment(0.5, 0.5, [second, first]);

        Assert.Equal(first.Id, found?.Id);
    }

    [Fact]
    public void IsInsideAnySegment_IsTheUnionOfThePolygons()
    {
        var left = Segment("Left", 0, 0.0, 1.0, x0: 0.0, x1: 0.4);
        var right = Segment("Right", 0, 0.0, 1.0, x0: 0.6, x1: 1.0);
        var segments = new[] { left, right };

        Assert.True(WallProjection.IsInsideAnySegment(0.2, 0.5, segments));
        Assert.True(WallProjection.IsInsideAnySegment(0.8, 0.5, segments));
        Assert.False(WallProjection.IsInsideAnySegment(0.5, 0.5, segments));
        Assert.False(WallProjection.IsInsideAnySegment(0.2, 0.5, []));
    }

    private static WallSegment Segment(
        string name,
        int angle,
        double y0,
        double y1,
        double x0 = 0.0,
        double x1 = 1.0,
        int sortOrder = 0) =>
        new()
        {
            Name = name,
            Angle = angle,
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
