// <copyright file="MarkerPlanValidatorTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;
using static Blocwerk.Core.Tests.MarkerPlanning.PlanFixtures;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>Each validation rule fires on the plan that breaks it — and the real wall passes.</summary>
public class MarkerPlanValidatorTests
{
    [Fact]
    public void Attic_AsBuilt_HasNoErrors_ButFlagsItsSmallOverhangCorners_AndTheSideTriangle()
    {
        var issues = MarkerPlanValidator.Validate(AtticMarkerPlan.Plan);

        Assert.DoesNotContain(issues, i => i.Severity == PlanIssueSeverity.Error);
        var small = issues.Where(i => i.Code == "marker-too-small").Select(i => i.MarkerId).Order().ToList();
        Assert.Equal<int?>([0, 1, 2, 3, 31, 32, 33], small);
        Assert.Contains("150 mm", issues.First(i => i.Code == "marker-too-small").Message);
        Assert.Equal(2, Assert.Single(issues, i => i.Code == "grazing-surface").Segment);
    }

    [Fact]
    public void DuplicateIds_IdsOutsideTheDictionary_AndUnknownSegments_AreErrors()
    {
        var plan = Plan([Rect(0, 3000, 2000)], [M(1, 0, 500, 500), M(1, 0, 1500, 500), M(50, 0, 2500, 500), M(2, 7, 100, 100)]);

        var codes = Codes(plan, PlanIssueSeverity.Error);

        Assert.Contains("marker-duplicate-id", codes);
        Assert.Contains("marker-id-range", codes);
        Assert.Contains("marker-segment", codes);
    }

    [Fact]
    public void MarkersOutsideTheSurface_OrOverlapping_AreErrors()
    {
        var plan = Plan([Tri(0, 2000, 2000, TriangleCorner.BottomLeft)], [M(0, 0, 1900, 1900), M(1, 0, 300, 300), M(2, 0, 350, 350), M(3, 0, 300, 1200)]);

        var codes = Codes(plan, PlanIssueSeverity.Error);

        Assert.Contains("marker-outside", codes);
        Assert.Contains("marker-overlap", codes);
    }

    [Fact]
    public void FewerThanThreeMarkers_IsAnError_BunchedMarkers_AWarning()
    {
        var plan = Plan(
            [Rect(0, 3000, 2000), Rect(1, 3000, 2000, new PlanAttachment(0, SegmentEdge.Right, SegmentEdge.Left, 0))],
            [M(0, 0, 200, 200), M(1, 0, 2800, 1800), M(2, 1, 200, 200), M(3, 1, 400, 200), M(4, 1, 300, 400)]);

        var issues = MarkerPlanValidator.Validate(plan);

        Assert.Equal(0, Assert.Single(issues, i => i.Code == "segment-few-markers").Segment);
        Assert.Equal(1, Assert.Single(issues, i => i.Code == "segment-bunched").Segment);
    }

    [Fact]
    public void MoreThan50Markers_AndAWrongDictionary_AreErrors()
    {
        var markers = Enumerable.Range(0, 51).Select(i => M(i, 0, 100 + (i * 110), 100)).ToList();
        var plan = Plan([Rect(0, 6000, 2000)], markers) with { Dictionary = "DICT_6X6_250" };

        var codes = Codes(plan, PlanIssueSeverity.Error);

        Assert.Contains("too-many-markers", codes);
        Assert.Contains("dictionary", codes);
    }

    [Fact]
    public void ImplausiblePhotoSetup_IsAnError()
    {
        var plan = Plan([Rect(0, 3000, 2000)], photo: new PhotoSetup(50, "custom", 200, 100));

        var codes = Codes(plan, PlanIssueSeverity.Error);

        Assert.Equal(["photo-distance", "photo-fov", "photo-resolution"], codes.Where(c => c.StartsWith("photo-", StringComparison.Ordinal)).Order());
    }

    [Fact]
    public void AMarkerlessSharedEdge_IsAWarning()
    {
        var plan = Plan(
            [Rect(0, 20000, 3000), Rect(1, 3000, 2000, new PlanAttachment(0, SegmentEdge.Bottom, SegmentEdge.Top, 0))],
            [M(0, 0, 19000, 2800), M(1, 0, 19800, 2800), M(2, 0, 19400, 2400), M(3, 1, 200, 200), M(4, 1, 2800, 200), M(5, 1, 1500, 1800)]);

        Assert.Contains(MarkerPlanValidator.Validate(plan), i => i.Code == "shared-edge-uncovered" && i.Segment == 1);
    }

    [Fact]
    public void Tips_AreWarnings_WithTipCodes()
    {
        var tips = MarkerPlanValidator.Validate(AtticMarkerPlan.Plan).Where(i => i.Code.StartsWith("tip-", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(tips);
        Assert.All(tips, t => Assert.Equal(PlanIssueSeverity.Warning, t.Severity));
    }

    private static PlanMarker M(int id, int segment, double x, double y) => new(id, segment, x, y, 100, MarkerRole.Corner);

    private static List<string> Codes(MarkerPlan plan, PlanIssueSeverity severity) =>
        MarkerPlanValidator.Validate(plan).Where(i => i.Severity == severity).Select(i => i.Code).ToList();
}
