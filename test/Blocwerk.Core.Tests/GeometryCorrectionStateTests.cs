// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Corrections;
using Blocwerk.Core.Capture.Coverage;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.CoverageFixtures;

namespace Blocwerk.Core.Tests;

/// <summary>
/// What the capture panel, the result card and the coverage report show for walls without markers, checked at the
/// service level (the repo has no component tests): the sources in words, the correction state, the draft's measured
/// distance and the coverage report's feature flag.
/// </summary>
public class GeometryCorrectionStateTests
{
    [Theory]
    [InlineData("estimate", false, "estimated (±10 %)")]
    [InlineData("measured", true, "measured")]
    [InlineData("anchors", true, "carried over from the model of 25 Sep 2026")]
    public void ScaleSource_InWords(string source, bool known, string expected)
    {
        var world = new WallGeometryWorld { FrameSource = "features", ScaleSource = source, ScaleKnown = known };

        Assert.Equal(expected, GeometrySourceText.Scale(world, new DateTimeOffset(2026, 9, 25, 22, 13, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("floor", true, "from the floor", true)]
    [InlineData("declared", true, "from the declared vertical surface", true)]
    [InlineData("cameras", false, "not measured", false)]
    public void GravitySource_InWords(string source, bool known, string expected, bool measured)
    {
        var world = new WallGeometryWorld { FrameSource = "features", GravitySource = source, GravityKnown = known };

        Assert.Equal(expected, GeometrySourceText.Gravity(world, null));
        Assert.Equal(!measured, GeometrySourceText.GravityUnknown(world));
    }

    [Fact]
    public void MarkerModel_SourcesAreTheMarkers()
    {
        Assert.Equal("from the printed markers", GeometrySourceText.Scale(new WallGeometryWorld { GravityKnown = true }, null));
        Assert.Equal("from the markers' surfaces", GeometrySourceText.Gravity(new WallGeometryWorld { GravityKnown = true }, null));
    }

    [Fact]
    public async Task State_OffersMakeExact_WithThePhotosThatHaveCameras_AndTheSurfaces()
    {
        using var h = new WallTestHarness();
        var (modelId, captureId) = await GeometryCorrectionFixture.SeedAsync(h);

        var state = (await GeometryCorrectionFixture.Service(h, new CorrectionFollowUpQueue()).GetStateAsync(h.WallId))!;

        Assert.Equal(modelId, state.ModelId);
        Assert.True(state.FromFeatures);
        Assert.True(state.ScaleIsEstimate);
        Assert.Equal("estimated (±10 %)", state.Scale);
        Assert.Equal(captureId, state.CaptureId);
        Assert.Equal([1, 2, 3], state.Photos.Select(p => p.Index));
        Assert.Equal(["0", "1"], state.Facets.Select(f => f.Id));
        Assert.True(state.Facets[0].IsReference);
        Assert.Null(state.LastCorrection);
    }

    [Fact]
    public async Task State_OfAnAnchoredModel_SaysWhichModelTheScaleCameFrom()
    {
        using var h = new WallTestHarness();
        var root = JsonNode.Parse(MarkerlessFixture.FeatureDoc(anchored: true))!;
        var referenceId = Guid.NewGuid();
        root["quality"]!["registration"] = new JsonObject { ["referenceModelId"] = referenceId.ToString() };
        await GeometryCorrectionFixture.SeedAsync(h, root.ToJsonString());
        await using (var db = h.CreateContext())
        {
            db.WallGeometryModels.Add(new WallGeometryModel
            {
                Id = referenceId, WallId = h.WallId, Json = MarkerlessFixture.MarkerDoc(), Source = "capture marker",
                CreatedAt = new DateTimeOffset(2026, 9, 25, 22, 13, 0, TimeSpan.Zero),
            });
            await db.SaveChangesAsync();
        }

        var state = (await GeometryCorrectionFixture.Service(h, new CorrectionFollowUpQueue()).GetStateAsync(h.WallId))!;

        Assert.False(state.ScaleIsEstimate);
        Assert.Equal("carried over from the model of 25 Sep 2026", state.Scale);
        Assert.Equal("carried over from the model of 25 Sep 2026", state.Gravity);
    }

    [Fact]
    public async Task Draft_KeepsAMeasuredDistance_RefusesABadOne_AndSendsItToTheFeatureSolve()
    {
        using var h = new WallTestHarness();
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector { Ids = [] });
        s.Client.GeometryJson = MarkerlessFixture.FeatureDoc(anchored: false);
        await h.SeedWallAsync(holdCount: 0);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        for (var i = 0; i < 3; i++)
        {
            await s.Service.AddPhotoAsync(draft.CaptureId, $"IMG_{i}.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(seed: 3 + i)), CancellationToken.None);
        }

        await GeometryCorrectionTests.Refused(
            () => s.Service.SetScaleReferenceAsync(draft.CaptureId, new CaptureScaleReference(1, [2, 2], [200, 2], 1000)), "on the photo");
        await s.Service.SetScaleReferenceAsync(draft.CaptureId, new CaptureScaleReference(1, [2, 2], [60, 40], 1000));

        var kept = (await s.Service.GetDraftAsync(h.WallId))!.ScaleReference!;
        Assert.Equal(1000, kept.Mm);
        Assert.Equal(new double[] { 60, 40 }, kept.B);

        Assert.Empty(await s.Service.StartAsync(draft.CaptureId, new CaptureDeclarations([], []), "no markers"));
        await s.Processor.ProcessAsync(draft.CaptureId, CancellationToken.None);
        var solve = s.Client.MultipartSubmissions.First(m => m.Kind == MarkerlessCaptureSupport.SolveKind);
        var measured = JsonNode.Parse(MarkerlessCaptureTests.RequestOf(solve))!["measuredDistance"]!;
        Assert.Equal("p01", measured["photo"]!.GetValue<string>());
        Assert.Equal(1000, measured["mm"]!.GetValue<double>());
    }

    [Fact]
    public async Task Draft_RemovingThePhoto_RemovesItsMeasuredDistance()
    {
        using var h = new WallTestHarness();
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector { Ids = [] });
        await h.SeedWallAsync(holdCount: 0);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        var photo = await s.Service.AddPhotoAsync(draft.CaptureId, "IMG_0.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(seed: 3)), CancellationToken.None);
        await s.Service.SetScaleReferenceAsync(draft.CaptureId, new CaptureScaleReference(photo.Index, [2, 2], [60, 40], 1000));

        await s.Service.RemovePhotoAsync(draft.CaptureId, photo.PhotoId);

        await using var db = h.CreateContext();
        Assert.Null((await db.WallCaptures.SingleAsync(c => c.Id == draft.CaptureId)).ScaleReferenceJson);
    }

    [Fact]
    public void CoverageReport_OfAFeatureModel_SaysSo_SoTheMarkerRowsBecomeOneTip()
    {
        var root = JsonNode.Parse(DocumentJson())!;
        root["world"] = new JsonObject { ["up"] = new JsonArray(0.0, 0.0, 1.0), ["frameSource"] = "features" };
        CoverageCamera[] photos = [Photo([1500, -2500, 1300], At(1500, 1300))];

        var feature = CaptureCoverageAnalyzer.Analyze(Inputs(WallGeometryDocument.Parse(root.ToJsonString()), photos), DateTimeOffset.UnixEpoch);
        var markers = CaptureCoverageAnalyzer.Analyze(Inputs(Document((1, 300, 300, 6)), photos), DateTimeOffset.UnixEpoch);

        Assert.True(feature.FromFeatures);
        Assert.DoesNotContain(feature.Advice, a => a.Kind == "markers");
        Assert.False(markers.FromFeatures);
        Assert.True(CaptureCoverageReport.Parse(feature.ToJson())!.FromFeatures);
    }
}
