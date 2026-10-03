// <copyright file="SlabPlanTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Web.Components.Shared.MarkerPlanner;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>
/// Slabs (negative overhang) are first-class: the sign survives JSON, layout, geometry import and the
/// solve request, and every label says "12° slab" rather than "-12° overhang".
/// </summary>
public class SlabPlanTests
{
    private static readonly string GeometryPath = Path.Combine(AppContext.BaseDirectory, "MarkerPlanning", "attic-wall-geometry.json");

    /// <summary>The Attic with its main wall turned into a 15° slab.</summary>
    internal static MarkerPlan SlabAttic { get; } = AtticMarkerPlan.Plan with
    {
        Segments = AtticMarkerPlan.Segments.Select(s => s.Index == 0 ? s with { OverhangDeg = -15 } : s).ToList(),
    };

    [Theory]
    [InlineData(-12, "12° slab")]
    [InlineData(-2, "2° slab")]
    [InlineData(-1.9, "vertical")]
    [InlineData(0, "vertical")]
    [InlineData(1.9, "vertical")]
    [InlineData(45, "45° overhang")]
    [InlineData(12.34, "12.3° overhang")]
    public void Describe_NamesTheLean_NeverASignedOverhang(double angle, string expected)
    {
        Assert.Equal(expected, SurfaceAngle.Describe(angle));
    }

    [Fact]
    public void Signed_PutsTheSignOnTheLean()
    {
        Assert.Equal(-15, SurfaceAngle.Signed(SurfaceLean.Slab, 15));
        Assert.Equal(-15, SurfaceAngle.Signed(SurfaceLean.Slab, -15));
        Assert.Equal(30, SurfaceAngle.Signed(SurfaceLean.Overhang, 30));
        Assert.Equal(0, SurfaceAngle.Signed(SurfaceLean.Vertical, 30));
        Assert.Equal(SurfaceLean.Slab, SurfaceAngle.LeanOf(-15));
    }

    [Fact]
    public void SlabPlan_RoundTripsThroughJson_WithItsSign()
    {
        var json = MarkerPlanJson.ToJson(SlabAttic);

        var back = MarkerPlanJson.FromJson(json, out var errors);

        Assert.Empty(errors);
        Assert.Contains("\"overhangDeg\": -15", json);
        Assert.Equal(-15, back!.Segments[0].OverhangDeg);
        Assert.Equal(json, MarkerPlanJson.ToJson(back));
    }

    [Theory]
    [InlineData(-90, true)]
    [InlineData(90, true)]
    [InlineData(-91, false)]
    [InlineData(120, false)]
    public void Json_AcceptsTiltsFromSlabToRoof_Symmetrically(double angle, bool ok)
    {
        var plan = SlabAttic with { Segments = SlabAttic.Segments.Select(s => s.Index == 0 ? s with { OverhangDeg = angle } : s).ToList() };

        MarkerPlanJson.FromJson(MarkerPlanJson.ToJson(plan), out var errors);

        Assert.Equal(ok, errors.Count == 0);
    }

    [Fact]
    public void Layout_KeepsTheSign_AndOnlyNearPlumbSurfacesAreGravityReferences()
    {
        var plan = SlabAttic with { Segments = SlabAttic.Segments.Select(s => s.Index == 1 ? s with { OverhangDeg = -1.5 } : s).ToList() };

        var layout = WallMarkerLayout.FromPlan(plan);

        Assert.Equal(-15, layout.FindSegment(0)!.OverhangDeg);
        Assert.False(layout.FindSegment(0)!.VerticalReference);
        Assert.Equal(-1.5, layout.FindSegment(1)!.OverhangDeg);
        Assert.True(layout.FindSegment(1)!.VerticalReference);
        Assert.Equal(SurfaceAngle.VerticalToleranceDeg, WallMarkerLayout.VerticalToleranceDeg);
    }

    [Fact]
    public void PlanFromGeometry_KeepsAMeasuredSlabsSign()
    {
        var json = File.ReadAllText(GeometryPath).Replace("45.415", "-12.5", StringComparison.Ordinal);

        var plan = MarkerPlanFromGeometry.Build(WallGeometryDocument.Parse(json), AtticMarkerPlan.Photo);

        Assert.Equal(-12.5, plan.Segments[0].OverhangDeg);
        Assert.Equal("12.5° slab", SurfaceAngle.Describe(plan.Segments[0].OverhangDeg));
    }

    [Fact]
    public void Pdf_LabelsASlabAsASlab()
    {
        var main = SlabAttic.Segments[0];

        Assert.Equal("main wall: rectangle 5200 × 3400 mm · 15° slab · yaw straight", MarkerPlanPdf.SurfaceLine(main));
        Assert.Equal("15° slab · yaw straight", MarkerPlanPdf.MapLabel(main));
        Assert.Equal("vertical · yaw 90° left", MarkerPlanPdf.MapLabel(SlabAttic.Segments[2]));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(MarkerPlanPdf.Render(SlabAttic, "Slab"), 0, 4));
    }

    [Fact]
    public void NetLabels_ReadSlab()
    {
        var net = NetLayout.Compute(SlabAttic).Net;

        var model = NetCanvasModel.Build(SlabAttic, net, MarkerGenerationOptions.Default);

        Assert.Equal("5200×3400 · 15° slab · yaw straight", model.Segments.Single(s => s.Index == 0).Detail);
        Assert.DoesNotContain(model.Segments, s => s.Detail.Contains('-'));
    }

    [Fact]
    public void PlacementCheck_NamesBothLeans()
    {
        var geometry = WallGeometryDocument.Parse(File.ReadAllText(GeometryPath));
        var detected = SlabAttic.Markers.Select(m => m.Id).ToHashSet();

        var check = MarkerPlacementChecker.Check(WallMarkerLayout.FromPlan(SlabAttic), geometry, detected);

        var mismatch = Assert.Single(check.Findings, f => f.Kind == MarkerPlacementIssue.AngleMismatch);
        Assert.Contains("measured 45.4° overhang, but the plan says 15° slab.", mismatch.Message);
    }

    [Fact]
    public void Sizing_IsSymmetric_AndANearFlatSlabIsShotFromAbove()
    {
        var photo = PlanFixtures.Photo;
        Assert.Equal(
            MarkerSizing.EstimatedPx(100, PlanFixtures.Rect(0, 1000, 1000, overhang: 15), photo),
            MarkerSizing.EstimatedPx(100, PlanFixtures.Rect(0, 1000, 1000, overhang: -15), photo),
            6);

        var plan = PlanFixtures.Plan([PlanFixtures.Rect(0, 2000, 1000, overhang: -80)]);
        var grazing = Assert.Single(MarkerPlanValidator.Validate(plan), i => i.Code == "grazing-surface");
        Assert.Contains("(80° slab)", grazing.Message);
        Assert.Contains("shoot down onto it from above", grazing.Message);
    }
}
