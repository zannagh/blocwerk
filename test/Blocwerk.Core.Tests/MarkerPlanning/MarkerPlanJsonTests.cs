// <copyright file="MarkerPlanJsonTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>The JSON that travels with photo dumps: round-trips, tolerant of extras, strict on numbers.</summary>
public class MarkerPlanJsonTests
{
    [Fact]
    public void Attic_RoundTrips_Exactly()
    {
        var json = MarkerPlanJson.ToJson(AtticMarkerPlan.Plan);

        var back = MarkerPlanJson.FromJson(json, out var errors);

        Assert.Empty(errors);
        Assert.NotNull(back);
        Assert.Equal(json, MarkerPlanJson.ToJson(back));
        Assert.Equal(AtticMarkerPlan.Plan.Markers, back.Markers);
    }

    [Fact]
    public void Json_IsCamelCase_WithStringEnums_AndAFormatTagFirst()
    {
        var json = MarkerPlanJson.ToJson(AtticMarkerPlan.Plan);

        Assert.StartsWith("{\n  \"format\": \"blocwerk-marker-plan\",\n  \"schemaVersion\": 1,", json.ReplaceLineEndings("\n"));
        Assert.Contains("\"shape\": \"triangle\"", json);
        Assert.Contains("\"rightAngle\": \"bottomLeft\"", json);
        Assert.Contains("\"ownEdge\": \"hypotenuse\"", json);
        Assert.Contains("\"role\": \"filler\"", json);
    }

    [Fact]
    public void UnknownFields_CommentsAndTrailingCommas_AreTolerated()
    {
        var node = JsonNode.Parse(MarkerPlanJson.ToJson(AtticMarkerPlan.Plan))!.AsObject();
        node["capturedWith"] = "a future field";
        node["photo"]!["lens"] = new JsonObject { ["model"] = "x" };
        var json = "// exported by a newer planner\n" + node.ToJsonString().TrimEnd('}') + ",}";

        Assert.NotNull(MarkerPlanJson.FromJson(json, out var errors));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("\"distanceMm\": \"2500\"")]
    [InlineData("\"distanceMm\": NaN")]
    [InlineData("\"distanceMm\": 1e400")]
    [InlineData("\"distanceMm\": -5")]
    public void BadNumbers_AreRefused_WithAReadableMessage(string distance)
    {
        var json = MarkerPlanJson.ToJson(AtticMarkerPlan.Plan).Replace("\"distanceMm\": 2500", distance);

        var plan = MarkerPlanJson.FromJson(json, out var errors);

        Assert.Null(plan);
        Assert.Contains(errors, e => e.Contains("distanceMm", StringComparison.OrdinalIgnoreCase) || e.Contains("not valid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void OutOfRangeMarkerFields_NameTheField()
    {
        var json = MarkerPlanJson.ToJson(AtticMarkerPlan.Plan).Replace("\"sizeMm\": 125", "\"sizeMm\": 5000");

        Assert.Null(MarkerPlanJson.FromJson(json, out var errors));
        Assert.Contains(errors, e => e.StartsWith("markers[0].sizeMm must be between 10 and 1000", StringComparison.Ordinal));
    }

    [Fact]
    public void NewerSchema_WrongFormat_Empty_AndOversized_AreRefused()
    {
        var json = MarkerPlanJson.ToJson(AtticMarkerPlan.Plan);

        Assert.Null(MarkerPlanJson.FromJson(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"), out var newer));
        Assert.Contains("newer Blocwerk", newer.Single());
        Assert.Null(MarkerPlanJson.FromJson(json.Replace("blocwerk-marker-plan", "wall-geometry"), out var format));
        Assert.Contains("not a marker plan", format.Single());
        Assert.Null(MarkerPlanJson.FromJson("  ", out _));
        Assert.Null(MarkerPlanJson.FromJson("[1,2]", out _));
        Assert.Null(MarkerPlanJson.FromJson(new string(' ', MarkerPlanJson.MaxBytes) + json, out var big));
        Assert.Contains("larger than", big.Single());
    }

    [Fact]
    public void UnknownEnumValue_IsRefused()
    {
        var json = MarkerPlanJson.ToJson(AtticMarkerPlan.Plan).Replace("\"shape\": \"triangle\"", "\"shape\": \"hexagon\"");

        Assert.Null(MarkerPlanJson.FromJson(json, out var errors));
        Assert.Contains("shape", Assert.Single(errors), StringComparison.Ordinal);
    }
}
