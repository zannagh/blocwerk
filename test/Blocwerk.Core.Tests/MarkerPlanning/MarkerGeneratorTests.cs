// <copyright file="MarkerGeneratorTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;
using static Blocwerk.Core.Tests.MarkerPlanning.PlanFixtures;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>The suggested layout: inside, apart, decodable, deterministic, validator-clean.</summary>
public class MarkerGeneratorTests
{
    [Fact]
    public void Attic_SuggestedLayout_HasNoErrors_AndEveryMarkerFitsItsSurface()
    {
        var plan = MarkerGenerator.Generate(AtticMarkerPlan.Blank, MarkerGenerationOptions.Default);

        Assert.InRange(plan.Markers.Count, 12, ArucoDict4X4.Count);
        Assert.Equal(Enumerable.Range(0, plan.Markers.Count), plan.Markers.Select(m => m.Id));
        Assert.DoesNotContain(MarkerPlanValidator.Validate(plan), i => i.Severity == PlanIssueSeverity.Error);
        Assert.DoesNotContain(MarkerPlanValidator.Validate(plan), i => i.Code == "marker-too-small");
        foreach (var m in plan.Markers)
        {
            var segment = plan.Segments.Single(s => s.Index == m.Segment);
            Assert.True(SegmentOutline.ContainsSquare(segment, new PlanVector(m.XMm, m.YMm), m.SizeMm / 2, MarkerGenerationOptions.Default.EdgeInsetMm - 0.1));
        }
    }

    [Fact]
    public void Attic_MainWall_GetsFourCorners_At150mm_AndTheKickboard_Smaller()
    {
        var plan = MarkerGenerator.Generate(AtticMarkerPlan.Blank, MarkerGenerationOptions.Default);

        var mainCorners = plan.Markers.Where(m => m.Segment == 0 && m.Role == MarkerRole.Corner).ToList();
        Assert.Equal(4, mainCorners.Count);
        Assert.All(mainCorners, m => Assert.Equal(150, m.SizeMm));
        Assert.All(plan.Markers.Where(m => m.Segment == 1 && m.Role == MarkerRole.Corner), m => Assert.Equal(100, m.SizeMm));
        Assert.Equal(3, plan.Markers.Count(m => m.Segment == 2 && m.Role == MarkerRole.Corner));
    }

    [Theory]
    [InlineData(TriangleCorner.BottomLeft)]
    [InlineData(TriangleCorner.BottomRight)]
    [InlineData(TriangleCorner.TopLeft)]
    [InlineData(TriangleCorner.TopRight)]
    public void Triangle_GetsAMarkerAtEachVertex_Inside(TriangleCorner corner)
    {
        var plan = MarkerGenerator.Generate(Plan([Tri(0, 2400, 1800, corner)]), MarkerGenerationOptions.Default);

        Assert.Equal(3, plan.Markers.Count(m => m.Role == MarkerRole.Corner));
        Assert.DoesNotContain(MarkerPlanValidator.Validate(plan), i => i.Severity == PlanIssueSeverity.Error);
    }

    [Fact]
    public void LongEdges_GetFillers_NoFurtherApartThanHalfAPhoto()
    {
        var plan = MarkerGenerator.Generate(Plan([Rect(0, 8000, 1000)]), MarkerGenerationOptions.Default);

        var spacing = MarkerSizing.MaxSpacingMm(plan.Photo);
        var bottomRow = plan.Markers.Where(m => m.YMm < 500).OrderBy(m => m.XMm).ToList();
        Assert.True(bottomRow.Count > 2);
        Assert.All(bottomRow.Zip(bottomRow.Skip(1)), pair => Assert.True(pair.Second.XMm - pair.First.XMm <= spacing + 1));
    }

    [Fact]
    public void Output_IsDeterministic()
    {
        var a = MarkerGenerator.Generate(AtticMarkerPlan.Blank, MarkerGenerationOptions.Default);
        var b = MarkerGenerator.Generate(AtticMarkerPlan.Blank, MarkerGenerationOptions.Default);

        Assert.Equal(MarkerPlanJson.ToJson(a), MarkerPlanJson.ToJson(b));
    }

    [Fact]
    public void AHugeWallFromClose_NeedsMoreThan50Ids_AndValidationSaysSo()
    {
        var close = MarkerCameraPresets.Create(MarkerCameraPresets.Phone1X, 800);

        var plan = MarkerGenerator.Generate(Plan([Rect(0, 12000, 4000)], photo: close), MarkerGenerationOptions.Default);

        Assert.True(plan.Markers.Count > ArucoDict4X4.Count);
        Assert.Contains(MarkerPlanValidator.Validate(plan), i => i.Code == "too-many-markers");
    }
}
