// <copyright file="MarkerPlannerEditingTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Web.Components.Shared.MarkerPlanner;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>
/// The planner UI's pure helpers: net ↔ segment coordinates (what a drag drop is converted with),
/// marker and segment edits, the distance text box and the canvas model.
/// </summary>
public class MarkerPlannerEditingTests
{
    [Fact]
    public void NetToSegment_InvertsTheNetLayout_ForEveryMarkerOfTheAttic()
    {
        var plan = AtticMarkerPlan.Plan;
        var net = NetLayout.Compute(plan).Net;

        foreach (var marker in plan.Markers)
        {
            var square = net.Markers.Single(m => m.Id == marker.Id);
            var centre = PlannerGeometry.Centre(square.CornersMm);
            var local = PlannerGeometry.NetToSegment(net.Segments.Single(s => s.Index == marker.Segment), centre.X, centre.Y);

            Assert.Equal(marker.XMm, local.X, 6);
            Assert.Equal(marker.YMm, local.Y, 6);
        }
    }

    [Fact]
    public void SegmentToNet_AndBack_RoundTrips_OnARotatedSegment()
    {
        var segment = new NetSegment(3, [], 1200, -450, 90);
        var net = PlannerGeometry.SegmentToNet(segment, 300, 80);

        Assert.Equal(1200 - 80, net.X, 6);
        Assert.Equal(-450 + 300, net.Y, 6);
        var back = PlannerGeometry.NetToSegment(segment, net.X, net.Y);
        Assert.Equal(300, back.X, 6);
        Assert.Equal(80, back.Y, 6);
    }

    [Fact]
    public void Snap_RoundsToFiveMillimetres()
    {
        var snapped = PlannerGeometry.Snap(new PlanVector(102.4, 97.6));

        Assert.Equal(100, snapped.X);
        Assert.Equal(100, snapped.Y);
    }

    [Fact]
    public void MoveToNet_DroppedOnAnotherSurface_ReassignsTheMarker_AndSnaps()
    {
        var plan = AtticMarkerPlan.Plan;
        var net = NetLayout.Compute(plan).Net;
        var kickboard = net.Segments.Single(s => s.Index == 1);
        var drop = PlannerGeometry.SegmentToNet(kickboard, 1002, 223);
        var id = plan.Markers.First(m => m.Segment == 0).Id;

        var moved = PlanMarkerEdits.MoveToNet(plan, net, id, drop.X, drop.Y).Markers.Single(m => m.Id == id);

        Assert.Equal(1, moved.Segment);
        Assert.Equal(1000, moved.XMm, 6);
        Assert.Equal(225, moved.YMm, 6);
    }

    [Fact]
    public void MoveToNet_DroppedOffTheNet_KeepsTheMarkersOwnSurface()
    {
        var plan = PlanFixtures.Plan([PlanFixtures.Rect(0, 2000, 1000)], [new PlanMarker(0, 0, 100, 100, 80, MarkerRole.Corner)]);
        var net = NetLayout.Compute(plan).Net;

        var moved = PlanMarkerEdits.MoveToNet(plan, net, 0, 2100, 500).Markers.Single();

        Assert.Equal(0, moved.Segment);
        Assert.Equal(2100, moved.XMm, 6);
    }

    [Fact]
    public void Add_TakesTheLowestFreeId_AndTheSurfacesFillerSize()
    {
        var plan = PlanFixtures.Plan(
            [PlanFixtures.Rect(0, 2000, 1000)],
            [new PlanMarker(0, 0, 100, 100, 125, MarkerRole.Corner), new PlanMarker(2, 0, 600, 100, 80, MarkerRole.Filler)]);

        var added = PlanMarkerEdits.Add(plan, 0, new PlanVector(501, 499), out var id);

        Assert.Equal(1, id);
        var marker = added.Markers.Single(m => m.Id == 1);
        Assert.Equal((500.0, 500.0, 80.0, MarkerRole.Filler), (marker.XMm, marker.YMm, marker.SizeMm, marker.Role));
    }

    [Theory]
    [InlineData(SegmentShape.Rectangle)]
    [InlineData(SegmentShape.Triangle)]
    public void AddSegment_OnEveryFreeEdge_LaysOutWithoutNetErrors(SegmentShape shape)
    {
        var plan = PlanSegmentEdits.NewPlan();
        for (var i = 0; i < 4; i++)
        {
            plan = PlanSegmentEdits.AddSegment(plan, 0, shape, out _);
        }

        Assert.Equal(4, plan.Segments.Select(s => s.AttachedTo?.ParentEdge).OfType<SegmentEdge>().Distinct().Count());
        var layout = NetLayout.Compute(plan);
        Assert.DoesNotContain(layout.Issues, i => i.Severity == PlanIssueSeverity.Error);
        Assert.Equal(5, layout.Net.Segments.Count);
    }

    [Fact]
    public void AddSegment_ToATriangle_UsesItsFreeLegThenTheHypotenuse()
    {
        var plan = PlanSegmentEdits.AddSegment(PlanSegmentEdits.NewPlan(), 0, SegmentShape.Triangle, out var triangle);
        plan = PlanSegmentEdits.AddSegment(plan, triangle, SegmentShape.Rectangle, out var below);
        plan = PlanSegmentEdits.AddSegment(plan, triangle, SegmentShape.Rectangle, out var slanted);

        Assert.Equal(SegmentEdge.Bottom, plan.Segments.Single(s => s.Index == below).AttachedTo!.ParentEdge);
        Assert.Equal(SegmentEdge.Hypotenuse, plan.Segments.Single(s => s.Index == slanted).AttachedTo!.ParentEdge);
        Assert.DoesNotContain(NetLayout.Compute(plan).Issues, i => i.Severity == PlanIssueSeverity.Error);
    }

    [Fact]
    public void DeleteSegment_RefusesTheRootAndParents_AndDropsTheMarkersOfALeaf()
    {
        var plan = PlanSegmentEdits.AddSegment(PlanSegmentEdits.NewPlan(), 0, SegmentShape.Rectangle, out var child);
        plan = PlanSegmentEdits.AddSegment(plan, child, SegmentShape.Rectangle, out var grandchild);
        plan = plan with { Markers = [new PlanMarker(0, grandchild, 100, 100, 80, MarkerRole.Corner)] };

        Assert.Null(PlanSegmentEdits.DeleteSegment(plan, 0, out var rootRefusal));
        Assert.NotNull(rootRefusal);
        Assert.Null(PlanSegmentEdits.DeleteSegment(plan, child, out var parentRefusal));
        Assert.Contains("surface", parentRefusal);

        var after = PlanSegmentEdits.DeleteSegment(plan, grandchild, out var none)!;
        Assert.Null(none);
        Assert.Equal([0, child], after.Segments.Select(s => s.Index));
        Assert.Empty(after.Markers);
    }

    [Theory]
    [InlineData("2500", 2500)]
    [InlineData("2500 mm", 2500)]
    [InlineData("2.5 m", 2500)]
    [InlineData("2,5m", 2500)]
    [InlineData("2.5", 2500)]
    [InlineData("250 cm", 2500)]
    [InlineData(" 3 M ", 3000)]
    [InlineData("300", 300)]
    public void DistanceParser_ReadsMillimetresAndMetres(string text, double expectedMm)
    {
        Assert.Equal(expectedMm, PhotoDistanceParser.ParseMm(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("far")]
    [InlineData("-2 m")]
    [InlineData("0")]
    [InlineData("2.5 ft")]
    public void DistanceParser_RefusesNonsense(string? text)
    {
        Assert.Null(PhotoDistanceParser.ParseMm(text));
    }

    [Fact]
    public void CanvasModel_FormatsInvariantly_AndFlagsSmallMarkers()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var plan = PlanFixtures.Plan(
                [PlanFixtures.Rect(0, 2000.5, 1000)],
                [new PlanMarker(0, 0, 100, 100, 150, MarkerRole.Corner), new PlanMarker(1, 0, 600, 100, 10, MarkerRole.Filler)]);
            var model = NetCanvasModel.Build(plan, NetLayout.Compute(plan).Net, MarkerGenerationOptions.Default);

            Assert.DoesNotContain(",", model.ViewBox);
            Assert.True(model.Markers.Single(m => m.Id == 0).MeetsTarget);
            Assert.False(model.Markers.Single(m => m.Id == 1).MeetsTarget);
            Assert.All(model.Markers, m => Assert.True(m.CentreY < 0));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
