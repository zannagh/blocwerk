// <copyright file="WallGeometryModelChecksTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The solver's <c>quality.checks</c>, <c>gravityDetail.splitReferences</c> and rejected detections are read
/// tolerantly (older documents have none of them) and turned into plain-language lines for the wall admin.
/// </summary>
public class WallGeometryModelChecksTests
{
    private const string KickboardWarning =
        "kickboard is declared vertical but is not flat: markers [3] (facet 1b) lie at 4.2° to markers [1, 2] (facet 1a).";

    [Fact]
    public void AnOlderDocument_WithoutChecks_ParsesAndListsOnlyTheMeasuredAngles()
    {
        var document = WallGeometryDocument.Parse(GlyphGeometryJson.Build());

        var checks = WallGeometryModelChecks.From(document);

        Assert.Null(document.Quality!.Checks);
        Assert.Null(document.Quality.GravityDetail);
        Assert.All(checks, c => Assert.Equal(WallGeometryModelCheck.KindSegmentAngle, c.Kind));
        Assert.Equal("Main wall 44.6° overhang (declared 45°)", checks[0].Message);
        Assert.Equal(WallGeometryModelCheck.Info, checks[0].Level);
        Assert.Equal("Right piece is folded: facet 5a 12.5° overhang, facet 5b vertical", checks[1].Message);
    }

    [Fact]
    public void ADocumentWithChecks_ParsesEveryField()
    {
        var document = WallGeometryDocument.Parse(WithChecks(withWarnings: true));
        var quality = document.Quality!;

        Assert.Equal(new[] { KickboardWarning }, quality.Checks!.Warnings);
        Assert.Equal<double?>(1.5, quality.Checks.DeclaredVsMeasuredDeg!["0"]);
        var borderline = Assert.Single(quality.Checks.BorderlineFacetDecisions!);
        Assert.Equal("split", borderline.Kind);
        Assert.Equal<double?>(3.9, borderline.MinPlaneAngleDeg);
        Assert.Equal(new[] { 14, 15 }, quality.Checks.LevelPairs![0].Pair);
        var split = Assert.Single(quality.GravityDetail!.SplitReferences!);
        Assert.Equal(("kickboard", 2), (split.Name, split.Pieces.Count));
        Assert.Equal<double?>(0.8, split.Pieces[0].Share);
        Assert.Equal<double?>(-1.2, split.Pieces[1].LeanDeg);
        Assert.Equal(17, Assert.Single(quality.RejectedObservations!).Id);
    }

    [Fact]
    public void TheSolversWarnings_AreShownVerbatim_WithoutRepeatingTheStructuredOnes()
    {
        var checks = WallGeometryModelChecks.FromJson(WithChecks(withWarnings: true));

        var warning = Assert.Single(checks, c => c.Kind == WallGeometryModelCheck.KindSolverWarning);
        Assert.Equal(KickboardWarning, warning.Message);
        Assert.Equal(WallGeometryModelCheck.Warning, warning.Level);
        Assert.DoesNotContain(checks, c => c.Kind is WallGeometryModelCheck.KindSplitReference or WallGeometryModelCheck.KindBorderlineFacet);
        Assert.Contains(checks, c => c.Kind == WallGeometryModelCheck.KindLevelPair && c.Message.Contains("differ in height by 3.2 mm", StringComparison.Ordinal));
        Assert.Contains(checks, c => c.Kind == WallGeometryModelCheck.KindMarkerSize && c.Level == WallGeometryModelCheck.Info);
        Assert.Contains(checks, c => c.Kind == WallGeometryModelCheck.KindRejectedObservation && c.Message.StartsWith("Ignored marker 17 in IMG_3.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutWarnings_TheSplitAndBorderlineSentencesAreBuiltFromTheStructuredFields()
    {
        var checks = WallGeometryModelChecks.FromJson(WithChecks(withWarnings: false));

        var split = Assert.Single(checks, c => c.Kind == WallGeometryModelCheck.KindSplitReference);
        Assert.StartsWith("Kickboard is declared vertical but is not flat: markers [3] (facet 1b) lie at 4.2° to markers [1, 2]", split.Message);
        Assert.Contains("(80 %)", split.Message);
        Assert.Contains("facet 1a +0.3°, facet 1b -1.2°", split.Message);
        var borderline = Assert.Single(checks, c => c.Kind == WallGeometryModelCheck.KindBorderlineFacet);
        Assert.StartsWith("Right piece: the facet decision is borderline. Its parts differ by 3.90° against the 4° fold threshold", borderline.Message);
    }

    [Fact]
    public void UnknownGravity_AndAFarOffAngle_AreWarnings()
    {
        var root = JsonNode.Parse(GlyphGeometryJson.Build())!;
        root["segments"]![0]!["declaredVsMeasuredDeg"] = -5.4;
        root["world"] = new JsonObject { ["gravityKnown"] = false };

        var checks = WallGeometryModelChecks.FromJson(root.ToJsonString());

        Assert.Equal(WallGeometryModelCheck.KindGravity, checks[0].Kind);
        Assert.Equal(WallGeometryModelCheck.Warning, checks[0].Level);
        Assert.Equal(WallGeometryModelCheck.Warning, checks.Single(c => c.Message.StartsWith("Main wall", StringComparison.Ordinal)).Level);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    public void AMissingOrBrokenModel_HasNoChecks(string? json)
    {
        Assert.Empty(WallGeometryModelChecks.FromJson(json));
    }

    [Theory]
    [InlineData(45.24, "45.2° overhang")]
    [InlineData(-3.0, "3.0° slab")]
    [InlineData(0.02, "vertical")]
    public void AngleText_NamesOverhangAndSlab(double degrees, string expected)
    {
        Assert.Equal(expected, WallGeometryModelChecks.AngleText(degrees));
    }

    /// <summary>The fixture document plus every check field the current solver writes (and keys the app ignores).</summary>
    internal static string WithChecks(bool withWarnings)
    {
        var root = JsonNode.Parse(GlyphGeometryJson.Build())!;
        root["segments"]!.AsArray().Add(new JsonObject { ["index"] = 1, ["name"] = "kickboard", ["facets"] = new JsonArray() });
        var checks = new JsonObject
        {
            ["declaredVsMeasuredDeg"] = new JsonObject { ["0"] = 1.5 },
            ["markerSideRmsErrMm"] = 0.9,
            ["markerSideMeanErrMm"] = -0.4,
            ["levelPairs"] = new JsonArray(new JsonObject { ["pair"] = new JsonArray(14, 15), ["heightDiffMm"] = -3.2 }),
            ["borderlineFacetDecisions"] = new JsonArray(new JsonObject
            {
                ["segment"] = 5, ["kind"] = "split", ["minPlaneAngleDeg"] = 3.9, ["foldDeg"] = 4.0,
            }),
            ["seg0DeclaredVsMeasuredDeg"] = 1.5,
        };
        if (withWarnings)
        {
            checks["warnings"] = new JsonArray(KickboardWarning);
        }

        var quality = root["quality"]!.AsObject();
        quality["gravity"] = "least squares over segment 1 vertical";
        quality["checks"] = checks;
        quality["gravityDetail"] = new JsonObject
        {
            ["constraints"] = new JsonArray("segment 1 vertical"),
            ["splitReferences"] = new JsonArray(new JsonObject
            {
                ["segment"] = 1, ["name"] = "kickboard", ["foldDeg"] = 4.2, ["pieceNormalsAngleDeg"] = 4.3,
                ["method"] = "whole-segment plane",
                ["pieces"] = new JsonArray(Piece("1a", [1, 2], 0.8, 0.3), Piece("1b", [3], 0.2, -1.2)),
            }),
        };
        quality["rejectedObservations"] = new JsonArray(new JsonObject
        {
            ["photo"] = "IMG_3.jpg", ["id"] = 17, ["views"] = 3, ["residualPx"] = 40.1, ["thresholdPx"] = 4.0,
            ["reason"] = "inconsistent-with-other-views", ["markerDropped"] = false,
        });
        return root.ToJsonString();
    }

    private static JsonObject Piece(string facet, int[] ids, double share, double lean) => new()
    {
        ["facet"] = facet,
        ["markerIds"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()),
        ["observations"] = 12,
        ["extentMm"] = 900.5,
        ["angleToSegmentPlaneDeg"] = 0.8,
        ["share"] = share,
        ["leanDeg"] = lean,
    };
}
