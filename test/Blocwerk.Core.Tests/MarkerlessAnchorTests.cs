// <copyright file="MarkerlessAnchorTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A markerless re-capture of a wall that already has a model: photos of the active model's capture go along as anchors,
/// and the new model is activated only when the solver anchored it and its surfaces continue the active facets. A
/// refused anchoring stores the model inactive, like a marker model that could not be registered.
/// </summary>
public class MarkerlessAnchorTests
{
    [Fact]
    public async Task AnchoredRecapture_ClaimsTheActiveFacets_AndIsActivated()
    {
        using var h = new WallTestHarness();
        var detector = new SwitchableMarkerDetector();
        using var s = MarkerlessFixture.Scenario(h, detector);
        var first = await FirstMarkerCaptureAsync(s);
        detector.Ids = [];
        s.Client.GeometryJson = MarkerlessFixture.FeatureDoc(anchored: true);
        var second = await MarkerlessFixture.StartAsync(s, seedWall: false);

        await s.Processor.ProcessAsync(second, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == second);
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.Equal(first, capture.AnchorCaptureId);
        var model = await db.WallGeometryModels.SingleAsync(m => m.IsActive);
        Assert.Equal(capture.GeometryModelId, model.Id);
        Assert.Equal(WallGeometryFrameSource.Features, model.FrameSource);

        var doc = WallGeometryDocument.Parse(model.Json);
        Assert.Equal(["0", "1", "5"], doc.Segments.SelectMany(g => g.Facets).Select(f => f.Id).Order());
        Assert.Equal(0, doc.FindFacet("0")!.Value.Segment.Index);
        Assert.Equal("main wall", doc.FindFacet("0")!.Value.Segment.Name);
        Assert.Equal(5, doc.FindFacet("5")!.Value.Segment.Index);
        Assert.Equal(6, doc.FindFacet("1")!.Value.Segment.Index);
        Assert.Empty(doc.Markers);
        var registration = JsonNode.Parse(model.Json)!["quality"]!["registration"]!;
        Assert.Equal("0", registration["claimedFacets"]!["0"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnchorsTheSolverRefused_StoreTheModelInactive_WithTheReason()
    {
        using var h = new WallTestHarness();
        var detector = new SwitchableMarkerDetector();
        using var s = MarkerlessFixture.Scenario(h, detector);
        var first = await FirstMarkerCaptureAsync(s);
        var markerModel = await ActiveModelIdAsync(h);
        detector.Ids = [];
        s.Client.GeometryJson = MarkerlessFixture.FeatureDoc(anchored: false, anchorReason: "3 anchors registered (need 6)");
        var second = await MarkerlessFixture.StartAsync(s, seedWall: false);

        await s.Processor.ProcessAsync(second, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == second);
        Assert.Equal(WallCaptureStatus.Failed, capture.Status);
        Assert.Contains("3 anchors registered (need 6)", capture.Error);
        Assert.Contains("NOT activated", capture.Error);
        Assert.Equal(first, capture.AnchorCaptureId);
        Assert.Equal(markerModel, await ActiveModelIdAsync(h));
        var stored = await db.WallGeometryModels.SingleAsync(m => m.Id == capture.GeometryModelId);
        Assert.False(stored.IsActive);
        Assert.Equal(WallGeometryFrameSource.Features, stored.FrameSource);

        // The anchors went into the reconstruction as a00.. and into the solve with the reference they are known in.
        var prepare = s.SplatClient.MultipartSubmissions.Single(m => m.Kind == WallCaptureProcessor.PrepareKind);
        Assert.Equal(["a00.jpg", "a01.jpg", "a02.jpg"], prepare.Parts.Where(p => p.FileName?.StartsWith('a') == true).Select(p => p.FileName).Order());
        var request = JsonNode.Parse(MarkerlessCaptureTests.RequestOf(s.Client.MultipartSubmissions.Single(m => m.Kind == MarkerlessCaptureSupport.SolveKind)))!;
        Assert.Equal(["a00", "a01", "a02"], request["anchors"]!.AsObject().Select(kv => kv.Key).Order());
        Assert.Equal(3, request["reference"]!["cameras"]!.AsArray().Count);
    }

    [Fact]
    public async Task AnActiveModelWithoutStoredPhotos_CannotAnchor_SoTheNewModelStaysInactive()
    {
        using var h = new WallTestHarness();
        var detector = new SwitchableMarkerDetector();
        using var s = MarkerlessFixture.Scenario(h, detector);
        var first = await FirstMarkerCaptureAsync(s);
        await using (var db = h.CreateContext())
        {
            foreach (var photo in await db.WallCapturePhotos.Where(p => p.CaptureId == first).ToListAsync())
            {
                s.Files.Delete(photo.StoredPath);
            }
        }

        detector.Ids = [];
        s.Client.GeometryJson = MarkerlessFixture.FeatureDoc(anchored: false);
        var second = await MarkerlessFixture.StartAsync(s, seedWall: false);

        await s.Processor.ProcessAsync(second, CancellationToken.None);

        await using var check = h.CreateContext();
        var capture = await check.WallCaptures.SingleAsync(c => c.Id == second);
        Assert.Equal(WallCaptureStatus.Failed, capture.Status);
        Assert.Contains("No photos of the current 3D model's capture are stored", capture.Error);
        Assert.Null(capture.AnchorCaptureId);
        Assert.False((await check.WallGeometryModels.SingleAsync(m => m.Id == capture.GeometryModelId)).IsActive);
    }

    /// <summary>A marker capture whose model has posed cameras p01..p03: the wall's active model.</summary>
    private static async Task<Guid> FirstMarkerCaptureAsync(CaptureScenario s)
    {
        s.Client.GeometryJson = MarkerlessFixture.MarkerDoc();
        var id = await s.StartCaptureAsync(photos: 3);
        await s.Processor.ProcessAsync(id, CancellationToken.None);
        await using var db = s.Harness.CreateContext();
        Assert.Equal(WallCaptureGeometryMode.Markers, (await db.WallCaptures.SingleAsync(c => c.Id == id)).GeometryMode);
        return id;
    }

    private static async Task<Guid> ActiveModelIdAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.WallGeometryModels.Where(m => m.IsActive).Select(m => m.Id).SingleAsync();
    }
}
