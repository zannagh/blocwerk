// <copyright file="MarkerlessGeometryTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Registration;

namespace Blocwerk.Core.Tests;

/// <summary>The pure parts of markerless captures: availability, anchor choice, the request, the plane claim, validation.</summary>
public class MarkerlessGeometryTests
{
    private static readonly string[] Grid = ["p01", "p02", "p03", "p04", "p05", "p06", "p07", "p08", "p09"];

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task Availability_NeedsSolveSfm_AndAPrepareWithSparseAndAnchors(bool solver, bool worker, bool expected)
    {
        var geometry = new FakeComputeJobClient { Kinds = solver ? ["solve", "solve-sfm"] : ["solve"] };
        var splat = new FakeComputeJobClient
        {
            Service = ComputeServiceKind.Splat,
            Kinds = [WallCaptureProcessor.PrepareKind],
            PrepareOutputs = worker ? ["sparse.zip", "anchors"] : ["sparse.zip"],
        };

        Assert.Equal(expected, await MarkerlessCaptureSupport.IsAvailableAsync(new FakeComputeJobClientFactory(geometry, splat), CancellationToken.None));
    }

    [Fact]
    public void Anchors_AreSpreadOverTheWall_AndTheSameEveryTime()
    {
        var reference = MarkerlessFixture.MarkerDoc(Grid);
        var available = Grid.ToHashSet();

        var picked = AnchorPhotoSelector.Select(reference, available, count: 4);

        Assert.Equal(4, picked.Distinct().Count());
        Assert.Equal(picked, AnchorPhotoSelector.Select(reference, available, count: 4));
        var xs = CoverageCamera(reference).Where(c => picked.Contains(c.Image)).Select(c => c.Centre[0]).ToList();
        Assert.True(xs.Max() - xs.Min() >= 2000, $"anchors bunched: x {xs.Min():F0}..{xs.Max():F0} mm");
    }

    [Fact]
    public void Anchors_OnlyFromStoredPhotos()
    {
        var picked = AnchorPhotoSelector.Select(MarkerlessFixture.MarkerDoc(Grid), new HashSet<string> { "p02", "p07" });

        Assert.Equal(["p02", "p07"], picked.Order());
        Assert.Empty(AnchorPhotoSelector.Select(MarkerlessFixture.MarkerDoc(Grid), new HashSet<string>()));
    }

    [Fact]
    public void Request_CarriesHintsScaleAndAnchors()
    {
        var scale = CaptureSfmDocuments.ParseScale("{\"photoIndex\":2,\"a\":[10,20],\"b\":[110,20],\"mm\":500}");
        var request = JsonNode.Parse(CaptureSfmDocuments.BuildRequest(
            [1, 2], [new CaptureAngleHint(0, "Main", 45)], scale, CaptureSfmDocuments.AnchorMap(["p03"]), MarkerlessFixture.MarkerDoc(), 125))!;

        Assert.Equal(["p01", "p02"], request["photos"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()));
        Assert.Equal(45, request["segments"]![0]!["declaredAngleDeg"]!.GetValue<double>());
        Assert.Equal("p02", request["measuredDistance"]!["photo"]!.GetValue<string>());
        Assert.Equal("p03", request["anchors"]!["a00"]!.GetValue<string>());
        Assert.NotNull(request["reference"]!["cameras"]);
        Assert.Null(CaptureSfmDocuments.ParseScale("{\"photoIndex\":1,\"a\":[1],\"b\":[2,2],\"mm\":5}"));
    }

    [Fact]
    public void PlaneClaim_KeepsTheReferenceIds_RebasesThem_AndCarriesTheRest()
    {
        var modelId = Guid.NewGuid();

        var result = WallFrameRegistrationWriter.RewriteFeatures(MarkerlessFixture.FeatureDoc(anchored: true), MarkerlessFixture.MarkerDoc(), modelId);

        Assert.Equal(new Dictionary<string, string> { ["0"] = "0" }, result.Claims);
        var doc = WallGeometryDocument.Parse(result.Json);
        Assert.True(doc.IsFeatureFrame);
        Assert.Empty(doc.Markers);
        Assert.Equal(44.9, doc.FindFacet("0")!.Value.Facet.MeasuredAngleDeg);
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, doc.FindFacet("0")!.Value.Facet.Origin);
        Assert.NotNull(doc.FindFacet("5"));
        Assert.Equal(modelId.ToString(), JsonNode.Parse(result.Json)!["quality"]!["registration"]!["referenceModelId"]!.GetValue<string>());
    }

    [Fact]
    public void PlaneClaim_SkipsASurfaceFarFromEveryReferencePlane()
    {
        var shifted = JsonNode.Parse(MarkerlessFixture.FeatureDoc(anchored: true))!;
        shifted["segments"]![0]!["facets"]![0]!["origin"] = new JsonArray(0.0, -400.0, 0.0);

        var result = WallFrameRegistrationWriter.RewriteFeatures(shifted.ToJsonString(), MarkerlessFixture.MarkerDoc(), Guid.NewGuid());

        Assert.Empty(result.Claims);
    }

    [Fact]
    public void Validator_AcceptsAFeatureModelWithoutMarkers_ButNotAMarkerModel()
    {
        Assert.Empty(WallGeometryValidator.Validate(WallGeometryDocument.Parse(MarkerlessFixture.FeatureDoc(anchored: false))));

        var noMarkers = JsonNode.Parse(MarkerlessFixture.MarkerDoc())!;
        noMarkers["markers"] = new JsonArray();
        Assert.Contains(
            WallGeometryValidator.Validate(WallGeometryDocument.Parse(noMarkers.ToJsonString())),
            e => e.Contains("no markers", StringComparison.Ordinal));
    }

    [Fact]
    public void World_TellsEstimatedScaleAndUnknownGravity()
    {
        var world = WallGeometryDocument.Parse(MarkerlessFixture.FeatureDoc(anchored: false, gravityKnown: false)).World!;

        Assert.True(world.IsFeatureFrame);
        Assert.True(world.ScaleIsEstimate);
        Assert.Equal("estimate", world.ScaleSource);
        Assert.False(world.GravityKnown);
        Assert.False(WallGeometryDocument.Parse(MarkerlessFixture.MarkerDoc()).World!.ScaleIsEstimate);
    }

    private static IEnumerable<(string Image, double[] Centre)> CoverageCamera(string json) =>
        Geometry.Footprints.SolvedCamera.ParseAll(json).Select(c => (c.Image, c.Centre));
}
