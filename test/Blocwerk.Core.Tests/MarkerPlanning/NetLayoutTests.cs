// <copyright file="NetLayoutTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;
using static Blocwerk.Core.Tests.MarkerPlanning.PlanFixtures;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>The unfolded net: children butt against the parent edge, outside it, offset along it.</summary>
public class NetLayoutTests
{
    [Fact]
    public void Root_SitsAtTheOrigin_Unrotated()
    {
        var net = NetLayout.Compute(Plan([Rect(0, 3000, 2000)])).Net;

        var root = Assert.Single(net.Segments);
        Assert.Equal((0, 0, 0), (root.OriginX, root.OriginY, root.RotationDeg));
        Assert.Equal((0, 0, 3000, 2000), (net.MinX, net.MinY, net.MaxX, net.MaxY));
    }

    [Theory]
    [InlineData(SegmentEdge.Bottom, SegmentEdge.Top, 100, 100, -500, 1100, 0)]
    [InlineData(SegmentEdge.Top, SegmentEdge.Bottom, 100, 100, 2000, 1100, 2500)]
    [InlineData(SegmentEdge.Right, SegmentEdge.Left, 100, 3000, 100, 4000, 600)]
    [InlineData(SegmentEdge.Left, SegmentEdge.Right, 100, -1000, 100, 0, 600)]
    public void Child_IsLaidOutsideTheParentEdge_AtItsOffset(
        SegmentEdge parentEdge, SegmentEdge ownEdge, double offset, double minX, double minY, double maxX, double maxY)
    {
        var plan = Plan([Rect(0, 3000, 2000), Rect(1, 1000, 500, new PlanAttachment(0, parentEdge, ownEdge, offset))]);

        var layout = NetLayout.Compute(plan);

        Assert.Empty(layout.Issues);
        var child = layout.Net.Segments.Single(s => s.Index == 1);
        Assert.Equal(minX, child.PolygonMm.Min(p => p[0]), 6);
        Assert.Equal(minY, child.PolygonMm.Min(p => p[1]), 6);
        Assert.Equal(maxX, child.PolygonMm.Max(p => p[0]), 6);
        Assert.Equal(maxY, child.PolygonMm.Max(p => p[1]), 6);
        Assert.True(SignedArea(child.PolygonMm) > 0, "net polygons are counter-clockwise");
    }

    [Theory]
    [InlineData(TriangleCorner.BottomLeft)]
    [InlineData(TriangleCorner.BottomRight)]
    [InlineData(TriangleCorner.TopLeft)]
    [InlineData(TriangleCorner.TopRight)]
    public void Triangle_HypotenuseOnTheParent_MeetsItEndToEnd(TriangleCorner corner)
    {
        // A 3-4-5 triangle hung on a 5000 mm vertical edge by its hypotenuse.
        var plan = Plan([Rect(0, 3000, 5000), Tri(1, 3000, 4000, corner, new PlanAttachment(0, SegmentEdge.Left, SegmentEdge.Hypotenuse, 0))]);

        var layout = NetLayout.Compute(plan);

        Assert.Empty(layout.Issues);
        var tri = layout.Net.Segments.Single(s => s.Index == 1);
        Assert.Equal(3, tri.PolygonMm.Count);
        Assert.True(SignedArea(tri.PolygonMm) > 0);
        Assert.Equal(6_000_000, SignedArea(tri.PolygonMm), 3);
        Assert.Contains(tri.PolygonMm, p => Near(p, 0, 0));
        Assert.Contains(tri.PolygonMm, p => Near(p, 0, 5000));
        Assert.All(tri.PolygonMm, p => Assert.True(p[0] <= 1e-6, "the triangle lies left of the edge"));
    }

    [Fact]
    public void RectangleOnATrianglesHypotenuse_IsTiltedAndOutside()
    {
        var plan = Plan([Tri(0, 3000, 3000, TriangleCorner.BottomLeft), Rect(1, 1000, 500, new PlanAttachment(0, SegmentEdge.Hypotenuse, SegmentEdge.Bottom, 0))]);

        var layout = NetLayout.Compute(plan);

        Assert.Empty(layout.Issues);
        var rect = layout.Net.Segments.Single(s => s.Index == 1);
        Assert.Equal(-45, rect.RotationDeg, 6);
        Assert.All(rect.PolygonMm, p => Assert.True(p[0] + p[1] >= 3000 - 1e-6, "outside the hypotenuse x + y = 3000"));
    }

    [Fact]
    public void Markers_AreMappedIntoNetSpace_WithEstimatedPixels()
    {
        var plan = Plan(
            [Rect(0, 3000, 2000), Rect(1, 1000, 500, new PlanAttachment(0, SegmentEdge.Top, SegmentEdge.Bottom, 0))],
            [new PlanMarker(7, 1, 100, 100, 100, MarkerRole.Corner)]);

        var marker = Assert.Single(NetLayout.Compute(plan).Net.Markers);

        // The child sits on top of the root (rotated 0): its (100, 100) is net (100, 2100).
        Assert.Contains(marker.CornersMm, p => Near(p, 50, 2150));
        Assert.Equal(100 * MarkerSizing.PxPerMm(plan.Photo), marker.EstimatedPx, 6);
    }

    [Fact]
    public void OverlappingChildren_AreAnErrorNotAnException()
    {
        var plan = Plan([
            Rect(0, 3000, 2000),
            Rect(1, 1000, 500, new PlanAttachment(0, SegmentEdge.Top, SegmentEdge.Bottom, 0)),
            Rect(2, 1000, 500, new PlanAttachment(0, SegmentEdge.Top, SegmentEdge.Bottom, 500)),
        ]);

        var issue = Assert.Single(NetLayout.Compute(plan).Issues);
        Assert.Equal("net-overlap", issue.Code);
    }

    [Fact]
    public void Cycles_DanglingParents_MissingEdges_AndExtraRoots_AreReported()
    {
        var plan = Plan([
            Rect(0, 3000, 2000),
            Rect(1, 500, 500, new PlanAttachment(2, SegmentEdge.Top, SegmentEdge.Bottom, 0)),
            Rect(2, 500, 500, new PlanAttachment(1, SegmentEdge.Top, SegmentEdge.Bottom, 0)),
            Rect(3, 500, 500, new PlanAttachment(9, SegmentEdge.Top, SegmentEdge.Bottom, 0)),
            Tri(4, 500, 500, TriangleCorner.BottomLeft, new PlanAttachment(0, SegmentEdge.Top, SegmentEdge.Top, 0)),
            Rect(5, 500, 500),
        ]);

        var layout = NetLayout.Compute(plan);

        var codes = layout.Issues.Select(i => i.Code).Order().ToList();
        Assert.Equal(["attachment-cycle", "attachment-cycle", "attachment-edge", "attachment-missing-parent", "net-several-roots"], codes);
        Assert.Equal([0], layout.Net.Segments.Select(s => s.Index));
    }

    private static bool Near(double[] p, double x, double y) => Math.Abs(p[0] - x) < 1e-6 && Math.Abs(p[1] - y) < 1e-6;
}
