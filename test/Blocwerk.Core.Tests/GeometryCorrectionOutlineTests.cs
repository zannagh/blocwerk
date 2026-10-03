// <copyright file="GeometryCorrectionOutlineTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Corrections;
using Blocwerk.Core.Entities;
using Blocwerk.Core.MarkerPlanning;
using static Blocwerk.Core.Tests.MarkerPlanning.PlanFixtures;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Make sizes exact" taps the model through the facets' real shapes: a plan triangle's cut-away half standing in front
/// of the wall does not catch a tap, so the measured distance is the one on the wall behind it.
/// </summary>
public class GeometryCorrectionOutlineTests
{
    [Fact]
    public async Task ATapThroughATrianglesCutAwayHalf_MeasuresTheWallBehindIt()
    {
        using var h = new WallTestHarness();
        await GeometryCorrectionFixture.SeedAsync(h, WithLeaningTriangle());
        await AddPlanAsync(h);

        // Pixel (1600, 750) sees the wall point (2300, 0, 800) through the triangle's rectangle 370 mm in front of it.
        var result = await GeometryCorrectionFixture.Service(h, new CorrectionFollowUpQueue())
            .MakeSizesExactAsync(h.WallId, new CaptureScaleReference(1, [1000, 750], [1600, 750], 1980));

        Assert.Equal(1.1, result.Scale, 6);
    }

    /// <summary>
    /// The feature document plus segment 9: a slab hinged on the wall along z = x − 1800, its b leaning out of the wall.
    /// Its seam with the wall is b = 0; the plan keeps the half with its bottom-right corner (b ≤ 0).
    /// </summary>
    private static string WithLeaningTriangle()
    {
        var root = JsonNode.Parse(MarkerlessFixture.FeatureDoc(anchored: false))!;
        root["segments"]!.AsArray().Add(new JsonObject
        {
            ["index"] = 9,
            ["name"] = "slab",
            ["facets"] = new JsonArray(new JsonObject
            {
                ["id"] = "9",
                ["origin"] = new JsonArray(1800.0, 0.0, 0.0),
                ["u"] = new JsonArray(Math.Sqrt(0.5), 0.0, Math.Sqrt(0.5)),
                ["v"] = new JsonArray(-0.5, -Math.Sqrt(0.5), 0.5),
                ["normal"] = new JsonArray(0.5, -Math.Sqrt(0.5), -0.5),
                ["extentMm"] = new JsonObject { ["aMin"] = 0.0, ["aMax"] = 1414.0, ["bMin"] = -600.0, ["bMax"] = 600.0 },
            }),
        });
        return root.ToJsonString();
    }

    private static async Task AddPlanAsync(WallTestHarness h)
    {
        var plan = Plan([Rect(0, 3000, 2500), Tri(9, 1414, 1200, TriangleCorner.BottomRight, new PlanAttachment(0, SegmentEdge.Right, SegmentEdge.Hypotenuse, 0))]);
        await using var db = h.CreateContext();
        db.WallMarkerPlans.Add(new WallMarkerPlan { WallId = h.WallId, Json = MarkerPlanJson.ToJson(plan), Revision = 1, IsCurrent = true });
        await db.SaveChangesAsync();
    }
}
