using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Locks down the segment hit-testing every wall renderer shares: overlapping polygons
/// resolve by sort order, and "inside a segment" is the union of the polygons.
/// </summary>
public class WallProjectionTests
{
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
        int sortOrder = 0,
        WallSegmentKind kind = WallSegmentKind.Wall) =>
        new()
        {
            Name = name,
            Angle = angle,
            SortOrder = sortOrder,
            Kind = kind,
            Points =
            [
                new ShapePoint { Dx = x0, Dy = y0 },
                new ShapePoint { Dx = x1, Dy = y0 },
                new ShapePoint { Dx = x1, Dy = y1 },
                new ShapePoint { Dx = x0, Dy = y1 },
            ],
        };
}
