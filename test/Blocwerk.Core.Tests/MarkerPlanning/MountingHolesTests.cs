// <copyright file="MountingHolesTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.MarkerPlanning;
using static Blocwerk.Core.Tests.MarkerPlanning.PlanFixtures;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>Mounting holes: JSON compatibility, size rules, tight placement around the square, and page layout.</summary>
public class MountingHolesTests
{
    public static TheoryData<double, double, double> SizeHoleHead => new()
    {
        { 50, 1, 1.5 },
        { 50, 3, 6 },
        { 50, 3.5, 15 },
        { 125, 3, 6 },
        { 200, 2.5, 15 },
    };

    [Fact]
    public void OldPlanWithoutPrint_ParsesAndRoundTripsUnchanged()
    {
        var json = MarkerPlanJson.ToJson(AtticMarkerPlan.Plan);

        var back = MarkerPlanJson.FromJson(json, out var errors);

        Assert.Empty(errors);
        Assert.Null(back!.Print);
        Assert.DoesNotContain("\"print\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Holes_RideInTheJson()
    {
        var plan = AtticMarkerPlan.Plan with { Print = new PrintOptions(new MountingHoles(true, 2.5, 5.5)) };

        var json = MarkerPlanJson.ToJson(plan);
        var back = MarkerPlanJson.FromJson(json, out var errors);

        Assert.Empty(errors);
        Assert.Equal(plan.Print, back!.Print);
        Assert.Contains("\"mountingHoles\"", json, StringComparison.Ordinal);
        Assert.Contains("\"screwHeadDiameterMm\": 5.5", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4, 8, "mounting hole")]
    [InlineData(3, 3.2, "screw head")]
    [InlineData(3, 16, "screw head")]
    public void BadSizes_AreRefused_OnParse_AndInValidation(double hole, double head, string expected)
    {
        var plan = AtticMarkerPlan.Plan with { Print = new PrintOptions(new MountingHoles(true, hole, head)) };

        var parsed = MarkerPlanJson.FromJson(MarkerPlanJson.ToJson(plan), out var errors);

        Assert.Null(parsed);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
        Assert.Contains(MarkerPlanValidator.Validate(plan), i => i.Code == "mounting-holes" && i.Severity == PlanIssueSeverity.Error);
    }

    [Fact]
    public void ScrewBiasTip_ShowsOnlyWithoutHoles()
    {
        var holes = AtticMarkerPlan.Plan with { Print = new PrintOptions(MountingHoles.Default) };
        var off = AtticMarkerPlan.Plan with { Print = new PrintOptions(MountingHoles.Default with { Enabled = false }) };

        Assert.Contains(MarkerPlanValidator.Validate(AtticMarkerPlan.Plan), i => i.Code == "tip-screw-bias" && i.Severity == PlanIssueSeverity.Warning);
        Assert.Contains(MarkerPlanValidator.Validate(off), i => i.Code == "tip-screw-bias");
        Assert.DoesNotContain(MarkerPlanValidator.Validate(holes), i => i.Code == "tip-screw-bias");
    }

    [Theory]
    [MemberData(nameof(SizeHoleHead))]
    public void EveryHead_SitsExactlyTheGapFromTheSquare_AndTheGapInsideTheCutLine(double size, double hole, double head)
    {
        var holes = new MountingHoles(true, hole, head);
        var r = head / 2;
        var border = MountingHoleLayout.BorderMm(holes);
        foreach (var (x, y) in MountingHoleLayout.HoleCentres(0, 0, size, holes))
        {
            // Distance from the head's centre to the black square (axis-aligned): its corner is the nearest point.
            var dx = Math.Max(0, Math.Max(-x, x - size));
            var dy = Math.Max(0, Math.Max(-y, y - size));
            Assert.Equal(r + holes.GapToMarkerMm, Math.Sqrt((dx * dx) + (dy * dy)), 9);

            // The head's farthest point on each axis is exactly the edge gap inside the cut line.
            Assert.Equal(border - holes.GapToEdgeMm, Math.Max(dx, dy) + r, 9);
            Assert.InRange(x, -border + r, size + border - r);
            Assert.InRange(y, -border + r, size + border - r);
        }
    }

    [Fact]
    public void OffsetFormula_ForThePlannedScrews()
    {
        // 3 mm screw with a 6 mm head, 1 mm gaps: d = (3 + 1)/√2, border = d + 3 + 1.
        var holes = MountingHoles.Default;
        Assert.Equal(4 / Math.Sqrt(2), MountingHoleLayout.CentreOffsetMm(holes), 9);
        Assert.Equal(4, MountingHoleLayout.DiagonalOffsetMm(holes), 9);
        Assert.Equal(6.83, MountingHoleLayout.BorderMm(holes), 2);
        Assert.Equal(138.66, 125 + (2 * MountingHoleLayout.BorderMm(holes)), 2);
        Assert.Equal(10.42, MountingHoleLayout.BorderMm(holes, 125.0 / 12), 2);
        Assert.Equal(12.24, MountingHoleLayout.BorderMm(holes with { GapToMarkerMm = 3, GapToEdgeMm = 5 }), 2);
    }

    [Fact]
    public void TinyMarkerWithBigHead_StillLaysOut_OnA4_WithoutIssues()
    {
        var holes = new MountingHoles(true, 3.5, 15);
        var plan = Plan([Rect(0, 1000, 1000)], [new PlanMarker(0, 0, 200, 200, 50, MarkerRole.Corner)]) with
        {
            Print = new PrintOptions(holes),
        };

        var printed = Assert.Single(MarkerPlanPdf.PrintedMarkers(plan));
        var page = MarkerPlanPdfLayout.Build(plan)[printed.PageIndex];
        var tile = Assert.Single(page.Tiles);

        Assert.Equal(210, page.WidthMm);
        Assert.Equal(MountingHoleLayout.BorderMm(holes), tile.BorderMm, 9);
        Assert.Equal(tile.BorderMm, printed.BorderMm, 9);
        Assert.DoesNotContain(MarkerPlanValidator.Validate(plan), i => i.Code == "mounting-holes");
    }

    [Fact]
    public void TightHoles_ShrinkTheCutOut_AndKeepBigMarkersOnA4()
    {
        var markers = new[] { 125.0, 150 }.Select((s, i) => new PlanMarker(i, 0, 300 + (i * 400), 300, s, MarkerRole.Corner)).ToList();
        var plain = Plan([Rect(0, 2000, 1000)], markers);
        var holes = plain with { Print = new PrintOptions(MountingHoles.Default) };

        var plainTiles = MarkerPlanPdfLayout.Build(plain).SelectMany(p => p.Tiles).ToList();
        var holeTiles = MarkerPlanPdfLayout.Build(holes).SelectMany(p => p.Tiles).ToList();
        var pages = MarkerPlanPdfLayout.Build(holes);

        Assert.All(holeTiles, t => Assert.True(t.BoxMm < plainTiles.Single(p => p.Marker.Id == t.Marker.Id).BoxMm));
        Assert.Equal(138.66, holeTiles.Single(t => t.Marker.Id == 0).BoxMm, 2);
        Assert.All(MarkerPlanPdf.PrintedMarkers(holes), m => Assert.Equal(210, pages[m.PageIndex].WidthMm));
    }

    [Fact]
    public void PlanWithoutGaps_ParsesWithOneMillimetreGaps()
    {
        var json = MarkerPlanJson.ToJson(AtticMarkerPlan.Plan with { Print = new PrintOptions(MountingHoles.Default) });
        var node = JsonNode.Parse(json)!;
        var mounting = node["print"]!["mountingHoles"]!.AsObject();
        mounting.Remove("gapToMarkerMm");
        mounting.Remove("gapToEdgeMm");

        var back = MarkerPlanJson.FromJson(node.ToJsonString(), out var errors);

        Assert.Empty(errors);
        Assert.Equal(1, back!.Print!.MountingHoles!.GapToMarkerMm);
        Assert.Equal(1, back.Print.MountingHoles.GapToEdgeMm);
    }

    [Theory]
    [InlineData(0.4, 1, "to the marker")]
    [InlineData(1, 10.5, "to the cut edge")]
    [InlineData(double.NaN, 1, "to the marker")]
    public void BadGaps_AreRefused_OnParse_AndInValidation(double toMarker, double toEdge, string expected)
    {
        var holes = MountingHoles.Default with { GapToMarkerMm = toMarker, GapToEdgeMm = toEdge };
        var plan = AtticMarkerPlan.Plan with { Print = new PrintOptions(holes) };

        Assert.Contains(holes.Problems(), p => p.Contains(expected, StringComparison.Ordinal));
        Assert.Contains(MarkerPlanValidator.Validate(plan), i => i.Code == "mounting-holes" && i.Severity == PlanIssueSeverity.Error);
        if (double.IsFinite(toMarker))
        {
            Assert.Null(MarkerPlanJson.FromJson(MarkerPlanJson.ToJson(plan), out var errors));
            Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(125, 1, 3, 142.7)]
    [InlineData(150, 1, 4.5, 170.7)]
    [InlineData(200, 1, 8, 227.7)]
    [InlineData(300, 7, 10, 340.1)]
    public void SafeGaps_ReachTheRecommendedBorder(double size, double toMarker, double toEdge, double cutOut)
    {
        // Photo scale unknown: sized for a 60 px marker, so 4 px of border is side/15.
        var safe = MountingHoleSafety.SafeGaps(size, MountingHoles.Default)!;

        Assert.Equal(toMarker, safe.GapToMarkerMm);
        Assert.Equal(toEdge, safe.GapToEdgeMm);
        Assert.Equal(cutOut, size + (2 * MountingHoleLayout.BorderMm(safe)), 1);
        Assert.Equal(MountingHoleBorder.Clear, MountingHoleSafety.Assess(size, safe));
        Assert.NotEqual(MountingHoleBorder.Clear, MountingHoleSafety.Assess(size, MountingHoles.Default));
        Assert.Empty(safe.Problems());
    }

    [Theory]
    [InlineData(125, 0, MountingHoleBorder.Thin)]
    [InlineData(125, 60.0 / 125, MountingHoleBorder.Thin)]
    [InlineData(125, 30.0 / 125, MountingHoleBorder.TooThin)]
    [InlineData(50, 0, MountingHoleBorder.Clear)]
    [InlineData(50, 40.0 / 50, MountingHoleBorder.Clear)]
    public void TightDefaults_AgainstTheRealPhotoLevels(double size, double photoPxPerMm, MountingHoleBorder expected)
    {
        // The 1 / 1 mm default leaves 6.8 mm: ≈3.3 px on a 125 mm marker at 60 px — over the 2 px minimum,
        // under the 4 px that dark holds and volumes need.
        Assert.Equal(expected, MountingHoleSafety.Assess(size, MountingHoles.Default, photoPxPerMm));
        Assert.Equal(expected != MountingHoleBorder.TooThin, MountingHoleSafety.IsSafe(size, MountingHoles.Default, photoPxPerMm));
        if (expected == MountingHoleBorder.Clear)
        {
            Assert.Equal(MountingHoles.Default, MountingHoleSafety.SafeGaps(size, MountingHoles.Default, photoPxPerMm));
        }
    }

    [Fact]
    public void SafeGaps_GrowWithThePhotoDistance_AndGiveUpBeyondTheGapRange()
    {
        // 125 mm at 60 px: 4 px is 8.3 mm of border; the 6.8 mm default needs the cut edge 3 mm out.
        var planned = MountingHoleSafety.SafeGaps(125, MountingHoles.Default, 60.0 / 125)!;
        Assert.Equal(1, planned.GapToMarkerMm);
        Assert.Equal(3, planned.GapToEdgeMm);

        // At 40 px: 12.5 mm → 7 mm to the edge; the 2 px minimum alone (6.25 mm) is met by the default.
        var far = MountingHoleSafety.SafeGaps(125, MountingHoles.Default, 40.0 / 125)!;
        Assert.Equal(1, far.GapToMarkerMm);
        Assert.Equal(7, far.GapToEdgeMm);
        Assert.Equal(MountingHoles.Default, MountingHoleSafety.SafeGaps(125, MountingHoles.Default, 40.0 / 125, MountingHoleSafety.MinBorderPx));
        Assert.Null(MountingHoleSafety.SafeGaps(125, MountingHoles.Default, 0.1));
    }

    [Fact]
    public void ThinHoles_Warn_WithPhotoPixels_AndTheDarkWallGaps_ClearIt()
    {
        // From 2.5 m the Attic's 6.8 mm default border is 2–4 photo px: fine on the plywood, thin on dark holds.
        var plan = AtticMarkerPlan.Plan with { Print = new PrintOptions(MountingHoles.Default) };
        var issues = MarkerPlanValidator.Validate(plan);
        Assert.DoesNotContain(issues, i => i.Code == "mounting-holes-tight");
        var warning = Assert.Single(issues, i => i.Code == "mounting-holes-thin");
        Assert.Equal(PlanIssueSeverity.Warning, warning.Severity);
        Assert.Contains("fine on the light wall", warning.Message, StringComparison.Ordinal);
        Assert.Contains("dark holds or volumes, gap to marker", warning.Message, StringComparison.Ordinal);

        var safeHoles = SafeFor(plan, MountingHoleSafety.RecommendedBorderPx);
        Assert.Contains($"gap to cut edge {safeHoles.GapToEdgeMm:0.#} mm", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerPlanValidator.Validate(plan with { Print = new PrintOptions(safeHoles) }), i => i.Code.StartsWith("mounting-holes-", StringComparison.Ordinal));
    }

    [Fact]
    public void TooThinHoles_Warn_Strongly_AndTheSafeGaps_ClearIt()
    {
        // From 5 m the default border is under 2 px on some surfaces.
        var far = AtticMarkerPlan.Plan with { Photo = MarkerCameraPresets.Create(MarkerCameraPresets.PhoneUltraWide, 5000) };
        var plan = far with { Print = new PrintOptions(MountingHoles.Default) };
        var issues = MarkerPlanValidator.Validate(plan);
        Assert.DoesNotContain(issues, i => i.Code == "mounting-holes-thin");
        var warning = Assert.Single(issues, i => i.Code == "mounting-holes-tight");
        Assert.Equal(PlanIssueSeverity.Warning, warning.Severity);
        Assert.Contains("px in your photos", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Gap to marker", warning.Message, StringComparison.Ordinal);

        var safeHoles = SafeFor(plan, MountingHoleSafety.RecommendedBorderPx);
        Assert.Contains($"gap to cut edge {safeHoles.GapToEdgeMm:0.#} mm", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerPlanValidator.Validate(plan with { Print = new PrintOptions(safeHoles) }), i => i.Code.StartsWith("mounting-holes-", StringComparison.Ordinal));
    }

    [Fact]
    public void Instructions_MentionTheHoles_OnlyWhenOn()
    {
        const string line = "Screw through the marked holes; keep screw heads inside the dashed circles.";
        var holes = AtticMarkerPlan.Plan with { Print = new PrintOptions(MountingHoles.Default) };

        Assert.Contains(MarkerPlanPdf.Instructions(holes), l => l.Contains(line, StringComparison.Ordinal));
        Assert.DoesNotContain(MarkerPlanPdf.Instructions(AtticMarkerPlan.Plan), l => l.Contains(line, StringComparison.Ordinal));
        Assert.Equal(3, JsonNode.Parse(MarkerPlanJson.ToJson(holes))!["print"]!["mountingHoles"]!["holeDiameterMm"]!.GetValue<double>());
    }

    private static MountingHoles SafeFor(MarkerPlan plan, double borderPx) =>
        plan.Markers.Aggregate(MountingHoles.Default, (holes, m) => MountingHoleSafety.SafeGaps(
            m.SizeMm, holes, MarkerSizing.EstimatedPx(1, plan.Segments.Single(s => s.Index == m.Segment), plan.Photo), borderPx)!);
}
