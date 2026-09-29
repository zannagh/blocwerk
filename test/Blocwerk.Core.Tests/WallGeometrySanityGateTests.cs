// <copyright file="WallGeometrySanityGateTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The activation gate: a model that measures its markers far off their printed size, puts a camera kilometres away,
/// tilts a surface far from its declared angle or reprojects badly is refused; a sound one passes.
/// </summary>
public class WallGeometrySanityGateTests
{
    [Fact]
    public void ASoundModel_Passes()
    {
        Assert.Empty(WallGeometrySanityGate.Problems(Model()));
    }

    [Fact]
    public void TheBrokenAtticModel_FailsEveryCheck()
    {
        // The 2026-09-29 model: main wall 17.2° slab (declared 45°), markers 52 m too big, a camera 881 km away, 6.0 px.
        var json = Model(mainWallDeg: -17.2, sideMeanErrMm: 52193.7, sideRmsErrMm: 352208.8, cameraZMm: 881_318_000, reprojPx: 5.97);

        var problems = WallGeometrySanityGate.Problems(json);

        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, p => p.Contains("+52193.7 mm off their printed size", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Main wall (facet 0) measures 17.2° slab, declared 45.0° overhang", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("p02: 881318 m", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("6.0 px", StringComparison.Ordinal));
        Assert.StartsWith("The solved model failed the sanity checks: ", WallGeometrySanityGate.Describe(problems));
    }

    [Theory]
    [InlineData(6.0, 2.0, false)] // 4.8 % of 125 mm
    [InlineData(6.5, 2.0, true)] // 5.2 %
    [InlineData(1.0, 9.9, false)] // spread 7.9 %
    [InlineData(1.0, 10.1, true)] // spread 8.1 %
    public void MarkerSizes_AreJudgedAgainstThePrintedSize(double meanErr, double rmsErr, bool refused)
    {
        var problems = WallGeometrySanityGate.Problems(Model(sideMeanErrMm: meanErr, sideRmsErrMm: rmsErr));

        Assert.Equal(refused, problems.Count == 1);
    }

    [Theory]
    [InlineData(54.9, false)]
    [InlineData(55.1, true)]
    public void AFacetFarFromItsDeclaredAngle_IsRefused(double measured, bool refused)
    {
        Assert.Equal(refused, WallGeometrySanityGate.Problems(Model(mainWallDeg: measured)).Count == 1);
    }

    [Fact]
    public void AVerticalReferenceSegment_IsJudgedAgainstPlumb()
    {
        var problems = WallGeometrySanityGate.Problems(Model(kickboardDeg: 15.3));

        Assert.Contains("Kickboard (facet 1) measures 15.3° overhang, declared vertical", Assert.Single(problems));
    }

    [Fact]
    public void AFeatureModel_IsNotJudged()
    {
        var node = JsonNode.Parse(Model(sideMeanErrMm: 500))!;
        node["world"]!["frameSource"] = "features";

        Assert.Empty(WallGeometrySanityGate.Problems(node.ToJsonString()));
    }

    private static string Model(
        double mainWallDeg = 45.8,
        double kickboardDeg = 0.4,
        double sideMeanErrMm = 0.8,
        double sideRmsErrMm = 6.0,
        double cameraZMm = 4000,
        double reprojPx = 3.1) => new JsonObject
        {
            ["version"] = 1,
            ["markerSizeMm"] = 125,
            ["world"] = new JsonObject { ["gravityKnown"] = true },
            ["segments"] = new JsonArray(
                Segment(0, "main wall", 45, false, mainWallDeg),
                Segment(1, "kickboard", 0, true, kickboardDeg)),
            ["markers"] = new JsonArray(Marker(0, 0, 0), Marker(1, 2000, 0)),
            ["cameras"] = new JsonArray(Camera("p01", 3000), Camera("p02", cameraZMm)),
            ["quality"] = new JsonObject
            {
                ["reprojRmsPx"] = reprojPx,
                ["checks"] = new JsonObject { ["markerSideMeanErrMm"] = sideMeanErrMm, ["markerSideRmsErrMm"] = sideRmsErrMm },
            },
        }.ToJsonString();

    private static JsonObject Segment(int index, string name, double declared, bool reference, double measured) => new()
    {
        ["index"] = index,
        ["name"] = name,
        ["declaredAngleDeg"] = declared,
        ["angleIsGravityReference"] = reference,
        ["measuredAngleDeg"] = measured,
        ["facets"] = new JsonArray(new JsonObject { ["id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture), ["measuredAngleDeg"] = measured }),
    };

    private static JsonObject Marker(int id, double x, double z) => new()
    {
        ["id"] = id,
        ["segment"] = 0,
        ["facet"] = "0",
        ["cornersPlaneMm"] = new JsonArray(Point(0, 0), Point(125, 0), Point(125, 125), Point(0, 125)),
        ["cornersWorldMm"] = new JsonArray(Point(x, 0, z), Point(x + 125, 0, z), Point(x + 125, 0, z + 125), Point(x, 0, z + 125)),
    };

    /// <summary>A camera looking along +y from <paramref name="distanceMm"/> in front of the wall (x_cam = R·X + t).</summary>
    private static JsonObject Camera(string image, double distanceMm) => new()
    {
        ["image"] = image,
        ["width"] = 4032,
        ["height"] = 3024,
        ["K"] = new JsonArray(3000, 0, 2016, 0, 3000, 1512, 0, 0, 1),
        ["dist"] = new JsonArray(0, 0, 0, 0, 0),
        ["R"] = new JsonArray(1, 0, 0, 0, 0, -1, 0, 1, 0),
        ["t"] = new JsonArray(0, 0, distanceMm),
    };

    private static JsonArray Point(params double[] values) => new(values.Select(v => (JsonNode?)v).ToArray());
}
