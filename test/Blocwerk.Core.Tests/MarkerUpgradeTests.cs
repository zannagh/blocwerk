// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Corrections;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The upgrade path between walls without and with markers. A marker capture of a wall whose active model is markerless:
/// the marker model defines the new frame and is activated (there are no markers to tie it to), the old model stays in the
/// history, the panels are untouched, and the follow-ups derive the holds' 3D data again for the new model. A later
/// markerless capture of a marker wall inherits the marker model's scale and "up" through the anchors.
/// </summary>
public class MarkerUpgradeTests
{
    private readonly IHoldTexturePlacementService placement = Substitute.For<IHoldTexturePlacementService>();
    private readonly IHoldFootprintService footprints = Substitute.For<IHoldFootprintService>();
    private readonly IHoldProtrusionService protrusion = Substitute.For<IHoldProtrusionService>();
    private readonly IWallVolumeService volumes = Substitute.For<IWallVolumeService>();
    private readonly IHoldProposalService proposals = Substitute.For<IHoldProposalService>();

    public MarkerUpgradeTests()
    {
        placement.PlaceFromPipelineAsync(default, default, default, default)
            .ReturnsForAnyArgs(new HoldPlacementResult(Guid.NewGuid(), 40, 0, 2, []));
        footprints.RefineFromPipelineAsync(default, default).ReturnsForAnyArgs(new HoldFootprintRunResult(30, 5, 7, 3));
        protrusion.MeasureFromPipelineAsync(default, default).ReturnsForAnyArgs(new HoldProtrusionRunResult(38, 2, 4, 38));
        volumes.DetectFromPipelineAsync(default, default).ReturnsForAnyArgs(new WallVolumeRunResult(2, 1, 6, 6));
        proposals.FindFromPipelineAsync(default, default).ReturnsForAnyArgs(new HoldProposalRunResult(3, 12, 4, 2, 0, "test"));
    }

    [Fact]
    public async Task AMarkerCapture_OfAMarkerlessWall_StartsANewFrame_AndDerivesTheHoldsAgain()
    {
        using var h = new WallTestHarness();
        var detector = new SwitchableMarkerDetector { Ids = [] };
        using var s = MarkerlessFixture.Scenario(h, detector, followUps: Chain);
        s.Client.GeometryJson = MarkerlessFixture.FeatureDoc(anchored: false);
        var markerless = await MarkerlessFixture.StartAsync(s);
        await s.Processor.ProcessAsync(markerless, CancellationToken.None);
        var (oldModel, panel, holds) = await SeedHoldsOnTheActiveModelAsync(h);
        foreach (var service in new object[] { placement, footprints, volumes, protrusion, proposals })
        {
            service.ClearReceivedCalls();
        }

        // The admin puts markers up and captures again.
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        detector.Ids = [0, 1, 2, 6, 7, 12];
        s.Client.GeometryJson = MarkerlessFixture.MarkerDoc();
        var upgrade = await StartMarkerCaptureAsync(s);
        await s.Processor.ProcessAsync(upgrade, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == upgrade);
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.Equal(WallCaptureGeometryMode.Markers, capture.GeometryMode);
        var active = await db.WallGeometryModels.SingleAsync(m => m.IsActive);
        Assert.Equal(capture.GeometryModelId, active.Id);
        Assert.Equal(WallGeometryFrameSource.Markers, active.FrameSource);
        Assert.True(FrameLineage.IsReset(active.Json));
        Assert.Equal(oldModel.ToString(), JsonNode.Parse(active.Json)!["quality"]!["frameReset"]!["previousModelId"]!.GetValue<string>());
        Assert.Null(RegisteredGeometry.Carried(active.Json).ReferenceModelId);

        // The old model stays in the history with its derived data; the panels and the holds' panel truth are untouched.
        var old = await db.WallGeometryModels.SingleAsync(m => m.Id == oldModel);
        Assert.False(old.IsActive);
        Assert.Equal(WallGeometryFrameSource.Features, old.FrameSource);
        Assert.Single(await db.WallVolumes.Where(v => v.GeometryModelId == oldModel).ToListAsync());
        var panelNow = await db.WallPanels.SingleAsync();
        Assert.Equal((panel.Id, panel.Generation), (panelNow.Id, panelNow.Generation));
        Assert.Equal(panel.Photo, panelNow.Photo);
        foreach (var hold in await db.Holds.ToListAsync())
        {
            var before = holds.Single(x => x.Id == hold.Id);
            Assert.Equal((before.X, before.Y, before.Radius, before.WallPanelId), (hold.X, hold.Y, hold.Radius, hold.WallPanelId));
        }

        // Every follow-up ran for the NEW model: placements, shapes, volumes, protrusion, proposals.
        await placement.Received(1).PlaceFromPipelineAsync(h.WallId, active.Id, h.Owner.Id, Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            placement.PlaceFromPipelineAsync(h.WallId, active.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
            footprints.RefineFromPipelineAsync(h.WallId, Arg.Any<CancellationToken>());
            volumes.DetectFromPipelineAsync(h.WallId, Arg.Any<CancellationToken>());
            protrusion.MeasureFromPipelineAsync(h.WallId, Arg.Any<CancellationToken>());
            proposals.FindFromPipelineAsync(h.WallId, Arg.Any<CancellationToken>());
        });
        var record = CaptureFollowUpRecord.Parse(capture.FollowUpJson);
        Assert.Null(record.CarriedFrom);
        Assert.All(
            new[] { "place-holds", "refine-footprints", "detect-volumes", "measure-protrusion", "find-hold-proposals" },
            key => Assert.Equal(CaptureFollowUpOutcome.Done, record.Find(key)!.Outcome));
        Assert.NotNull(await db.WallGeometrySplats.SingleOrDefaultAsync(x => x.GeometryModelId == active.Id));
    }

    [Fact]
    public async Task AMarkerlessCapture_OfAMarkerWall_InheritsScaleAndUp_ThroughTheAnchorFit()
    {
        using var h = new WallTestHarness();
        var detector = new SwitchableMarkerDetector();
        using var s = MarkerlessFixture.Scenario(h, detector);
        s.Client.GeometryJson = MarkerlessFixture.MarkerDoc();
        var first = await s.StartCaptureAsync(photos: 3);
        await s.Processor.ProcessAsync(first, CancellationToken.None);
        var markerModel = await ActiveIdAsync(h);

        // The markers come down; the solver fits the anchors close enough to measure with, not to take over the frame.
        detector.Ids = [];
        s.Client.GeometryJson = AnchorFitDoc();
        var second = await MarkerlessFixture.StartAsync(s, seedWall: false);
        await s.Processor.ProcessAsync(second, CancellationToken.None);

        // The solve was given the marker model as the reference: its cameras for the anchors, its known gravity.
        var request = JsonNode.Parse(MarkerlessCaptureTests.RequestOf(
            s.Client.MultipartSubmissions.Single(m => m.Kind == MarkerlessCaptureSupport.SolveKind)))!;
        Assert.True(request["reference"]!["world"]!["gravityKnown"]!.GetValue<bool>());
        Assert.Equal(3, request["anchors"]!.AsObject().Count);

        // Stored inactive (the frame is refused), but its sizes and angles are the marker model's: after the admin
        // activates it from the history, the result card says so (no "≈" estimate, angles known).
        await using (var db = h.CreateContext())
        {
            var capture = await db.WallCaptures.SingleAsync(c => c.Id == second);
            Assert.Equal(WallCaptureStatus.StoredNotActivated, capture.Status);
            Assert.Equal(markerModel, await ActiveIdAsync(h));
            var stored = WallGeometryDocument.Parse((await db.WallGeometryModels.SingleAsync(m => m.Id == capture.GeometryModelId)).Json);
            Assert.False(stored.World!.ScaleIsEstimate);
            Assert.Equal(("anchor-fit", "anchor-fit"), (stored.World.ScaleSource, stored.World.GravitySource));
            await WallGlyphSettingsTests.Service(h).ActivateGeometryAsync(capture.GeometryModelId!.Value);
        }

        var state = await CorrectionService(h).GetStateAsync(h.WallId);
        Assert.NotNull(state);
        Assert.StartsWith("carried over from", state.Scale);
        Assert.False(state.ScaleIsEstimate);
        Assert.StartsWith("carried over from", state.Gravity);
        Assert.True(state.GravityKnown);
    }

    /// <summary>A feature model whose anchors the solver refused for the frame but used for scale and gravity.</summary>
    private static string AnchorFitDoc()
    {
        var doc = JsonNode.Parse(MarkerlessFixture.FeatureDoc(anchored: false, anchorReason: "anchor rms 45.1 mm > 35 mm"))!.AsObject();
        var world = doc["world"]!.AsObject();
        (world["scaleKnown"], world["scaleSource"], world["gravitySource"], world["gravityKnown"]) = (true, "anchor-fit", "anchor-fit", true);
        doc["quality"]!["sfm"]!["anchors"]!["usedFor"] = "scale and gravity";
        return doc.ToJsonString();
    }

    private static async Task<Guid> ActiveIdAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.WallGeometryModels.Where(m => m.IsActive).Select(m => m.Id).SingleAsync();
    }

    /// <summary>A panel with three holds placed on the active (markerless) model, and one volume on it.</summary>
    private static async Task<(Guid Model, WallPanel Panel, List<Hold> Holds)> SeedHoldsOnTheActiveModelAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var model = await db.WallGeometryModels.SingleAsync(m => m.IsActive);
        var panel = new WallPanel { WallId = h.WallId, Col = 0, Row = 0, Photo = [9, 9, 9], PhotoContentType = "image/jpeg", Generation = 0 };
        db.WallPanels.Add(panel);
        var holds = Enumerable.Range(0, 3).Select(i => new Hold
        {
            WallId = h.WallId, WallPanelId = panel.Id, X = 0.2 + (0.2 * i), Y = 0.5, Radius = 0.03, FacetId = "0",
            PlaneAMm = 500 + (800 * i), PlaneBMm = 1200, MetricSource = Detection.Enrichment.HoldMetric.TextureRegistration,
        }).ToList();
        db.Holds.AddRange(holds);
        db.WallVolumes.Add(new WallVolume
        {
            WallId = h.WallId, GeometryModelId = model.Id, FacetId = "0", Index = 1, FootprintJson = "[[0,0],[300,0],[300,300]]",
            SurfaceJson = "{}", AreaM2 = 0.05, HeightMm = 90, Confidence = 0.8,
        });
        await db.SaveChangesAsync();
        return (model.Id, panel, holds);
    }

    /// <summary>A marker capture of the already seeded wall (three photos, the suggested declarations).</summary>
    private static async Task<Guid> StartMarkerCaptureAsync(CaptureScenario s)
    {
        var draft = await s.Service.CreateDraftAsync(s.Harness.WallId);
        for (var i = 0; i < 3; i++)
        {
            await s.Service.AddPhotoAsync(draft.CaptureId, $"IMG_M{i}.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(seed: 20 + i)), CancellationToken.None);
        }

        var suggested = await s.Service.SuggestDeclarationsAsync(draft.CaptureId);
        var declarations = new CaptureDeclarations(
            suggested.Segments.Select(g => g with { VerticalReference = g.Index != 0, DeclaredAngleDeg = g.Index == 0 ? 45 : 0 }).ToList(),
            CaptureDeclarationRules.ParseLevelPairs("14-15").Pairs);
        Assert.Empty(await s.Service.StartAsync(draft.CaptureId, declarations, "markers are up"));
        return draft.CaptureId;
    }

    private static WallGeometryCorrectionService CorrectionService(WallTestHarness h) => new(
        h.DbContextFactory, h.CurrentUser, new CorrectionFollowUpQueue(), NullLogger<WallGeometryCorrectionService>.Instance);

    private CaptureFollowUpChain Chain(WallTestHarness harness) => FollowUpChains.Build(
        harness.RootContextFactory,
        new PlaceHoldsFollowUpStep(placement),
        new RefineFootprintsFollowUpStep(footprints),
        new DetectVolumesFollowUpStep(volumes),
        new MeasureProtrusionFollowUpStep(protrusion),
        new FindHoldProposalsFollowUpStep(proposals, harness.DbContextFactory));
}
