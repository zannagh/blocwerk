// <copyright file="CapturePlanLayoutCheckTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>
/// The per-photo plan check: a marker whose detection contradicts where the plan puts it relative to the other markers
/// of its segment (a hold decoded as an id) is ignored; markers only roughly where planned, ambiguous cases and walls
/// without a plan are left alone.
/// </summary>
public class CapturePlanLayoutCheckTests
{
    /// <summary>Six markers on a 4 × 3 m segment, ids 0..5, 100 mm, plus id 6 on another segment.</summary>
    private static readonly WallMarkerLayout Layout = WallMarkerLayout.FromPlan(new MarkerPlan(
        1,
        "DICT_4X4_50",
        new PhotoSetup(3000, "phone-1x", 70, 4032),
        [
            new PlanSegment(0, "main wall", SegmentShape.Rectangle, 4000, 3000, TriangleCorner.BottomLeft, 30, 0, null),
            new PlanSegment(1, "kickboard", SegmentShape.Rectangle, 4000, 300, TriangleCorner.BottomLeft, 0, 0, new PlanAttachment(0, SegmentEdge.Bottom, SegmentEdge.Top, 0)),
        ],
        [
            new PlanMarker(0, 0, 500, 500, 100, MarkerRole.Corner),
            new PlanMarker(1, 0, 2000, 500, 100, MarkerRole.Filler),
            new PlanMarker(2, 0, 3500, 500, 100, MarkerRole.Corner),
            new PlanMarker(3, 0, 500, 2500, 100, MarkerRole.Corner),
            new PlanMarker(4, 0, 2000, 2500, 100, MarkerRole.Filler),
            new PlanMarker(5, 0, 3500, 2500, 100, MarkerRole.Corner),
            new PlanMarker(6, 1, 2000, 150, 100, MarkerRole.Filler),
        ]));

    [Fact]
    public void MarkersWhereThePlanSays_AreAllKept()
    {
        var markers = Ids(0, 1, 2, 3, 4, 5, 6).Select(id => At(id)).ToList();

        var verdict = CapturePlanLayoutCheck.Apply(Layout, markers, null);

        Assert.Empty(verdict.Ignored);
        Assert.Equal(7, verdict.Kept.Count);
    }

    [Fact]
    public void MarkersPlacedRoughly_AreKept()
    {
        // Owners stick markers up to a few hundred mm from the planned spot (The Attic: up to ~500 mm).
        var markers = new[] { At(0, 300, -200), At(1, -250, 150), At(2, 200, 300), At(3, -300, -100), At(4, 350, -250), At(5) }.ToList();

        Assert.Empty(CapturePlanLayoutCheck.Apply(Layout, markers, null).Ignored);
    }

    [Fact]
    public void AMarkerOnTheWrongSideOfTheOthers_IsIgnored()
    {
        // "Id 2" decoded on a hold left of marker 0, while the plan puts 2 at the far right.
        var markers = new[] { At(0), At(1), At(3), At(4), At(2, -4500, 1000) }.ToList();

        var verdict = CapturePlanLayoutCheck.Apply(Layout, markers, null);

        var ignored = Assert.Single(verdict.Ignored);
        Assert.Equal(2, ignored.Marker.Id);
        Assert.Contains("mirrored against", ignored.Detail);
        Assert.Equal([0, 1, 3, 4], verdict.Kept.Select(m => m.Id));
    }

    [Fact]
    public void AMarkerFarFromWhereTheOthersPlaceIt_IsIgnored()
    {
        // Id 5 detected 1.5 m below its planned spot, without flipping any triangle.
        var markers = new[] { At(0), At(1), At(2), At(3), At(4), At(5, 0, -1500) }.ToList();

        var verdict = CapturePlanLayoutCheck.Apply(Layout, markers, null);

        var ignored = Assert.Single(verdict.Ignored);
        Assert.Equal(5, ignored.Marker.Id);
        Assert.Contains("from where markers", ignored.Detail);
    }

    [Fact]
    public void WithFourMarkersAWrongOneCannotBeToldApart_SoNothingIsIgnored()
    {
        // 2 flips the same triangles as 3 does: which one is wrong is the solver's call.
        var markers = new[] { At(0), At(1), At(3), At(2, -4500, 1000) }.ToList();

        Assert.Empty(CapturePlanLayoutCheck.Apply(Layout, markers, null).Ignored);
    }

    [Fact]
    public void AMarkerFarTooSmallForItsNeighbours_IsIgnored()
    {
        // Three 100 mm markers 300 mm apart: two look 100 px (3 m away at f = 3000 px), "id 2" 25 px (12 m away).
        var close = WallMarkerLayout.FromPlan(Layout.Plan! with
        {
            Markers = [new PlanMarker(0, 0, 500, 500, 100, MarkerRole.Corner), new PlanMarker(1, 0, 800, 500, 100, MarkerRole.Filler),
                new PlanMarker(2, 0, 650, 760, 100, MarkerRole.Filler)],
        });
        var markers = new[] { Square(0, 1000, 1000, 100), Square(1, 1300, 1000, 100), Square(2, 1150, 740, 25) }.ToList();

        var verdict = CapturePlanLayoutCheck.Apply(close, markers, 3000);

        var ignored = Assert.Single(verdict.Ignored);
        Assert.Equal(2, ignored.Marker.Id);
        Assert.Contains("mm from the camera", ignored.Detail);
        Assert.Empty(CapturePlanLayoutCheck.Apply(close, markers, null).Ignored);
    }

    [Fact]
    public void WithoutAPlan_NothingIsChecked()
    {
        var markers = new[] { At(0), At(1), At(3), At(4), At(2, -4500, 1000) }.ToList();

        Assert.Empty(CapturePlanLayoutCheck.Apply(WallMarkerLayout.Legacy(125), markers, null).Ignored);
    }

    private static int[] Ids(params int[] ids) => ids;

    /// <summary>A marker detected where plan point (planned + offset) projects, with the size it would appear there.</summary>
    private static CaptureMarker At(int id, double dx = 0, double dy = 0)
    {
        var planned = Layout.Markers[id];
        var (cx, cy) = (planned.PlannedXMm!.Value + dx, planned.PlannedYMm!.Value + dy + (planned.Segment == 1 ? -400 : 0));
        double[][] plan = [[cx - 50, cy + 50], [cx + 50, cy + 50], [cx + 50, cy - 50], [cx - 50, cy - 50]];
        var corners = plan.Select(p => Project(p[0], p[1])).ToArray();
        return new CaptureMarker(id, corners, false, MeanSide(corners));
    }

    /// <summary>A perspective view: plan mm (y up) → image px (y down), farther to the right.</summary>
    private static double[] Project(double x, double y)
    {
        var w = 1 + (0.0001 * x);
        return [(800 + (0.5 * x) + (0.08 * x)) / w, (2400 - (0.5 * y)) / w];
    }

    private static CaptureMarker Square(int id, double cx, double cy, double side)
    {
        double[][] corners = [[cx - (side / 2), cy - (side / 2)], [cx + (side / 2), cy - (side / 2)], [cx + (side / 2), cy + (side / 2)], [cx - (side / 2), cy + (side / 2)]];
        return new CaptureMarker(id, corners, false, side);
    }

    private static double MeanSide(double[][] c) => Enumerable.Range(0, 4)
        .Average(i => Math.Sqrt(Math.Pow(c[(i + 1) % 4][0] - c[i][0], 2) + Math.Pow(c[(i + 1) % 4][1] - c[i][1], 2)));
}
