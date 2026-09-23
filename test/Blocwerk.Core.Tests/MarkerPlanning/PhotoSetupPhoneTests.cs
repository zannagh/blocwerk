// <copyright file="PhotoSetupPhoneTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.MarkerPlanning;
using static Blocwerk.Core.Tests.MarkerPlanning.PlanFixtures;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>The phone model / lens / closest distance on <see cref="PhotoSetup"/>: additive JSON, validated, sized.</summary>
public class PhotoSetupPhoneTests
{
    [Fact]
    public void OldPlans_WithoutTheNewFields_ReadAndWriteAsBefore()
    {
        var json = MarkerPlanJson.ToJson(AtticMarkerPlan.Plan);

        Assert.DoesNotContain("phoneModel", json);
        Assert.DoesNotContain("nearestDistanceMm", json);
        var back = MarkerPlanJson.FromJson(json, out var errors)!;
        Assert.Empty(errors);
        Assert.Null(back.Photo.PhoneModel);
        Assert.Null(back.Photo.Lens);
        Assert.Equal(PhoneCameraCatalog.GenericPhoneId, PhoneCameraCatalog.Resolve(back.Photo)!.Value.Phone.Id);
    }

    [Fact]
    public void NewFields_RoundTrip()
    {
        var plan = AtticMarkerPlan.Plan with { Photo = Phone("iphone-16-pro", "1.2x", 3000, 1200) };

        var json = MarkerPlanJson.ToJson(plan);
        var back = MarkerPlanJson.FromJson(json, out var errors)!;

        Assert.Empty(errors);
        Assert.Contains("\"phoneModel\": \"iphone-16-pro\"", json);
        Assert.Contains("\"lens\": \"1.2x\"", json);
        Assert.Equal(plan.Photo, back.Photo);
        Assert.Equal(json, MarkerPlanJson.ToJson(back));
    }

    [Theory]
    [InlineData("phoneModel", "iPhone 16 Pro!", "photo.phoneModel")]
    [InlineData("lens", "wide", "photo.lens must")]
    public void MalformedIds_AreParseErrors(string field, string value, string message)
    {
        var node = JsonNode.Parse(MarkerPlanJson.ToJson(AtticMarkerPlan.Plan with { Photo = Phone("iphone-16-pro", "1x", 3000) }))!.AsObject();
        node["photo"]![field] = value;

        Assert.Null(MarkerPlanJson.FromJson(node.ToJsonString(), out var errors));
        Assert.Contains(errors, e => e.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void ALensWithoutAPhone_OrAClosestPastTheFarthest_IsAParseError()
    {
        var node = JsonNode.Parse(MarkerPlanJson.ToJson(AtticMarkerPlan.Plan))!.AsObject();
        node["photo"]!["lens"] = "1x";
        node["photo"]!["nearestDistanceMm"] = 9000;

        Assert.Null(MarkerPlanJson.FromJson(node.ToJsonString(), out var errors));
        Assert.Contains(errors, e => e.Contains("needs photo.phoneModel", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("nearestDistanceMm must not be larger", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownPhone_FromANewerCatalog_StillReads_AndOnlyWarns()
    {
        var plan = Plan([Rect(0, 3000, 2000)], photo: new PhotoSetup(3000, "phone-1x", 70, 4032, "iphone-99", "1x"));

        var back = MarkerPlanJson.FromJson(MarkerPlanJson.ToJson(plan), out var errors);
        var issues = MarkerPlanValidator.Validate(plan);

        Assert.NotNull(back);
        Assert.Empty(errors);
        Assert.Contains(issues, i => i.Code == "photo-camera-unknown" && i.Severity == PlanIssueSeverity.Warning);
    }

    [Fact]
    public void AStoredFovThatDisagreesWithTheLens_IsFlagged()
    {
        var plan = Plan([Rect(0, 3000, 2000)], photo: Phone("iphone-16-pro", "0.5x", 3000) with { HorizontalFovDeg = 80 });

        Assert.Contains(MarkerPlanValidator.Validate(plan), i => i.Code == "photo-camera-mismatch");
        Assert.DoesNotContain(MarkerPlanValidator.Validate(plan with { Photo = Phone("iphone-16-pro", "0.5x", 3000) }), i => i.Code.StartsWith("photo-camera", StringComparison.Ordinal));
    }

    [Fact]
    public void Generator_UsesSmallerMarkersCloser_AndLargerOnTheUltraWideFar()
    {
        var wall = Plan([Rect(0, 4000, 3000)]);

        double Largest(PhotoSetup photo) => MarkerGenerator.Generate(wall with { Photo = photo }, MarkerGenerationOptions.Default).Markers.Max(m => m.SizeMm);
        double Area(PhotoSetup photo) => MarkerFootprint.Of(MarkerGenerator.Generate(wall with { Photo = photo }, MarkerGenerationOptions.Default)).AreaCm2;

        var farUw = Largest(Phone("iphone-16-pro", "0.5x", 3000));
        var nearUw = Largest(Phone("iphone-16-pro", "0.5x", 1500));
        var farMain = Largest(Phone("iphone-16-pro", "1x", 3000));

        Assert.Equal(200, farUw);
        Assert.Equal(50, nearUw);
        Assert.Equal(80, farMain);
        Assert.True(Area(Phone("iphone-16-pro", "1.2x", 3000)) < Area(Phone("iphone-16-pro", "0.5x", 3000)) / 3);
    }

    [Fact]
    public void Generator_MixesSizes_FillersSmallerThanCorners()
    {
        var plan = MarkerGenerator.Generate(Plan([Rect(0, 4000, 3000)], photo: Phone("iphone-16-pro", "0.5x", 1500)), MarkerGenerationOptions.Default);

        Assert.All(plan.Markers.Where(m => m.Role == MarkerRole.Corner), m => Assert.Equal(50, m.SizeMm));
        Assert.All(plan.Markers.Where(m => m.Role == MarkerRole.Filler), m => Assert.Equal(30, m.SizeMm));
        var footprint = MarkerFootprint.Of(plan);
        Assert.True(footprint.Saving > 0.2);
        Assert.DoesNotContain(MarkerPlanValidator.Validate(plan), i => i.Code == "marker-too-small");
    }

    private static PhotoSetup Phone(string id, string lens, double mm, double? near = null)
    {
        var phone = PhoneCameraCatalog.Find(id)!;
        return MarkerCameraPresets.ForPhone(phone, phone.Lens(lens)!, mm, near);
    }
}
