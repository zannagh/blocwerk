// <copyright file="WallMarkerLayoutTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>
/// The effective marker layout: without a plan it must be EXACTLY the old id arithmetic (The Attic runs
/// on it today); with a plan, ids, segments, sizes, positions and declarations come from the plan.
/// </summary>
public class WallMarkerLayoutTests
{
    [Fact]
    public void Legacy_IsTheOldArithmetic_ForEveryAtticId()
    {
        var layout = WallMarkerLayout.Legacy(125);
        string[] roles = ["TL", "TR", "BR", "BL", "H", "V"];

        for (var id = 0; id <= 35; id++)
        {
            Assert.Equal(id / 6, layout.SegmentOf(id));
            Assert.Equal(125, layout.SizeOf(id));
            Assert.Equal(roles[id % 6], layout.Markers[id].Role);
            Assert.Null(layout.Markers[id].PlannedXMm);
        }

        // The old SegmentOf divided ANY id; ids outside 0..35 never pass detection, but keep the arithmetic.
        Assert.Equal(7, layout.SegmentOf(44));
        Assert.True(layout.AllowedIds.SetEquals(MarkerDetectionOptions.DefaultAllowedIds));
        Assert.Same(MarkerDetectionOptions.Default, layout.DetectionOptions);
        Assert.Equal(CaptureDeclarationRules.MaxMarkerId, layout.MaxMarkerId);
        Assert.Equal("segment*6+role", layout.IdScheme);
        Assert.Equal([12, 13, 14, 15, 16, 17], layout.IdsOn(2));
        Assert.Empty(layout.Segments);
        Assert.Empty(layout.SharedEdges);
    }

    [Fact]
    public void Legacy_WithoutAStatedSize_KnowsNoSize()
    {
        var layout = WallMarkerLayout.Legacy(null);

        Assert.Null(layout.DefaultSizeMm);
        Assert.Null(layout.SizeOf(3));
    }

    [Fact]
    public void AtticPlan_PutsTheSparesOnTheMainWall_AndDeclaresItsSurfaces()
    {
        var layout = WallMarkerLayout.FromPlan(AtticMarkerPlan.Plan);

        Assert.True(layout.IsFromPlan);
        Assert.Equal("plan", layout.IdScheme);
        Assert.Equal(AtticMarkerPlan.Plan.Markers.Select(m => m.Id).Order(), layout.AllowedIds.Order());
        Assert.Equal(layout.AllowedIds, layout.DetectionOptions.AllowedIds);
        Assert.Equal(0, layout.SegmentOf(24));
        Assert.Equal(5, layout.SegmentOf(31));
        Assert.Null(layout.SegmentOf(11));
        Assert.Equal(125, layout.DefaultSizeMm);
        Assert.Equal(33, layout.MaxMarkerId);

        Assert.Equal([1, 2], layout.Segments.Where(s => s.VerticalReference).Select(s => s.Index));
        Assert.Equal(45, layout.FindSegment(0)!.OverhangDeg);
        Assert.Equal(90, layout.FindSegment(2)!.YawDeg);
        Assert.Equal(3, layout.FindSegment(2)!.OutlineMm.Count);
        Assert.Contains(layout.SharedEdges, e => e is { Segment: 2, Edge: SegmentEdge.Hypotenuse, ParentSegment: 0, ParentEdge: SegmentEdge.Left });
        Assert.Equal(3, layout.SharedEdges.Count);

        var marker0 = layout.Markers[0];
        Assert.Equal((85.1, 3158.9, "corner"), (marker0.PlannedXMm!.Value, marker0.PlannedYMm!.Value, marker0.Role));
    }

    [Fact]
    public void SuggestedPlan_KeepsEveryMarkersOwnSize_AndAllowsOnlyItsIds()
    {
        var plan = LoadPlan("attic-suggested.json");
        var layout = WallMarkerLayout.FromPlan(plan);

        foreach (var marker in plan.Markers)
        {
            Assert.Equal(marker.SizeMm, layout.SizeOf(marker.Id));
            Assert.Equal(marker.Segment, layout.SegmentOf(marker.Id));
        }

        var mostCommon = plan.Markers.GroupBy(m => m.SizeMm).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
        Assert.Equal(mostCommon, layout.DefaultSizeMm);
        Assert.Equal(plan.Markers.Select(m => m.Id).ToHashSet(), layout.DetectionOptions.AllowedIds.ToHashSet());
        Assert.DoesNotContain(49, layout.DetectionOptions.AllowedIds);
    }

    [Fact]
    public void Resolver_FallsBackToLegacy_WithoutAUsablePlan()
    {
        var json = File.ReadAllText(Fixture("attic-plan.json"));

        Assert.True(WallMarkerLayoutResolver.Resolve(json, 125).IsFromPlan);
        Assert.False(WallMarkerLayoutResolver.Resolve(null, 125).IsFromPlan);
        Assert.False(WallMarkerLayoutResolver.Resolve("{ not json", 125).IsFromPlan);
        Assert.Equal(100, WallMarkerLayoutResolver.Resolve(null, 100).SizeOf(4));
    }

    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "MarkerPlanning", name);

    internal static MarkerPlan LoadPlan(string name) =>
        MarkerPlanJson.FromJson(File.ReadAllText(Fixture(name)), out var errors) ?? throw new InvalidOperationException(string.Join(" ", errors));
}
