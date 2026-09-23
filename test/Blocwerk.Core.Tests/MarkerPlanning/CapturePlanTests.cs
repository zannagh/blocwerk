// <copyright file="CapturePlanTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.MarkerPlanning;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>
/// The plan JSON that travels with a photo dump: it decides which ids detection accepts, pre-fills the
/// declarations, goes into the solve request, and is checked against the solve afterwards. Walls
/// without a plan run exactly as before.
/// </summary>
public class CapturePlanTests
{
    private static readonly string AtticPlanJson = File.ReadAllText(WallMarkerLayoutTests.Fixture("attic-plan.json"));

    [Fact]
    public async Task UploadedPlan_BecomesTheWallsPlan_AndDrivesTheWholeCapture()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var draftId = await OpenDraftAsync(h, s);

        var attached = await s.Service.AttachPlanAsync(draftId, AtticPlanJson);
        await s.Service.AddPhotoAsync(draftId, "a.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(1)), CancellationToken.None);
        await s.Service.AddPhotoAsync(draftId, "b.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(2)), CancellationToken.None);

        Assert.True(attached.Accepted);
        Assert.Contains(attached.Notes, n => n.Contains("Saved as this wall's marker plan"));
        Assert.NotNull(await s.MarkerPlans.GetPlanAsync(h.WallId));
        var detector = (FakeMarkerDetectionService)s.Detector;
        Assert.All(detector.Calls, o => Assert.Contains(33, o!.AllowedIds));
        Assert.All(detector.Calls, o => Assert.DoesNotContain(11, o!.AllowedIds));

        var draft = (await s.Service.GetDraftAsync(h.WallId))!;
        Assert.Equal((true, 4, 21), (draft.Plan!.Uploaded, draft.Plan.Segments, draft.Plan.Markers));
        var suggested = await s.Service.SuggestDeclarationsAsync(draftId);
        Assert.Equal(
            [(0, "main wall", 45.0, false), (1, "kickboard", 0.0, true), (2, "left triangle", 0.0, true)],
            suggested.Segments.Select(d => (d.Index, d.Name, d.DeclaredAngleDeg!.Value, d.VerticalReference)));

        Assert.Empty(await s.Service.StartAsync(draftId, suggested, null));
        await s.Processor.ProcessAsync(draftId, CancellationToken.None);

        var request = JsonNode.Parse(s.Client.JsonSubmissions.Single(j => j.Kind == "solve").Json)!;
        Assert.Equal("plan", (string?)request["idScheme"]);
        Assert.Equal(0, (int)request["markerSegments"]!["24"]!);
        var summary = await s.Service.GetCaptureAsync(draftId);
        Assert.NotNull(summary!.PlacementCheck);
        Assert.Equal(21, summary.PlacementCheck!.PlannedMarkers);
        Assert.Contains(summary.PlacementCheck.Findings, f => f is { Kind: MarkerPlacementIssue.NeverSeen, MarkerId: 3 });
    }

    [Fact]
    public async Task SlabPlan_DeclaresItsSurfaceNegative_AllTheWayIntoTheSolveRequest()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var draftId = await OpenDraftAsync(h, s);

        var attached = await s.Service.AttachPlanAsync(draftId, MarkerPlanJson.ToJson(SlabPlanTests.SlabAttic));
        await s.Service.AddPhotoAsync(draftId, "a.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(1)), CancellationToken.None);
        await s.Service.AddPhotoAsync(draftId, "b.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(2)), CancellationToken.None);
        var suggested = await s.Service.SuggestDeclarationsAsync(draftId);

        Assert.True(attached.Accepted, string.Join("\n", attached.Errors));
        Assert.Equal((-15.0, false), (suggested.Segments[0].DeclaredAngleDeg!.Value, suggested.Segments[0].VerticalReference));
        Assert.Empty(CaptureDeclarationRules.Validate(suggested, null));
        Assert.Empty(await s.Service.StartAsync(draftId, suggested, null));
        await s.Processor.ProcessAsync(draftId, CancellationToken.None);

        var request = JsonNode.Parse(s.Client.JsonSubmissions.Single(j => j.Kind == "solve").Json)!;
        var main = request["segments"]!.AsArray().Single(n => (int)n!["index"]! == 0)!;
        Assert.Equal(-15, (double)main["declaredAngleDeg"]!);
        Assert.False((bool)main["verticalReference"]!);
    }

    [Fact]
    public async Task UploadedPlan_DifferentFromTheWallsPlan_BecomesItsNextRevision_AndTheCaptureRecordsIt()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var draftId = await OpenDraftAsync(h, s);
        var saved = await s.MarkerPlans.SavePlanAsync(h.WallId, AtticMarkerPlan.Plan);
        Assert.True(saved.Saved, string.Join("\n", saved.Issues.Select(i => i.Message)));
        var other = AtticMarkerPlan.Plan with { Segments = AtticMarkerPlan.Segments.Select(g => g with { Name = g.Name + " (new)" }).ToList() };

        var attached = await s.Service.AttachPlanAsync(draftId, MarkerPlanJson.ToJson(other));
        var again = await s.Service.AttachPlanAsync(draftId, MarkerPlanJson.ToJson(other));

        Assert.True(attached.Accepted, string.Join("\n", attached.Errors));
        Assert.Contains(attached.Notes, n => n.Contains("Saved as revision 2"));
        Assert.Contains(again.Notes, n => n.Contains("is the wall's saved marker plan (revision 2)"));
        Assert.Equal("main wall (new)", (await s.MarkerPlans.GetPlanAsync(h.WallId))!.Segments[0].Name);
        Assert.Equal([2, 1], (await s.MarkerPlans.GetRevisionsAsync(h.WallId)).Select(r => r.Revision));
        await using var db = h.CreateContext();
        Assert.Equal(2, (await db.WallCaptures.SingleAsync(c => c.Id == draftId)).PlanRevision);
    }

    [Fact]
    public async Task PlanWithNewIds_ClearsEarlierDetections_SoThePipelineLooksAgain()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var draftId = await OpenDraftAsync(h, s);
        await s.Service.AddPhotoAsync(draftId, "a.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(1)), CancellationToken.None);
        var renumbered = AtticMarkerPlan.Plan with
        {
            Markers = AtticMarkerPlan.Plan.Markers.Select(m => m.Id == 24 ? m with { Id = 44 } : m).ToList(),
        };

        var attached = await s.Service.AttachPlanAsync(draftId, MarkerPlanJson.ToJson(renumbered));

        Assert.Contains(attached.Notes, n => n.Contains("searched for the plan's markers again"));
        await using var db = h.CreateContext();
        Assert.Null((await db.WallCapturePhotos.SingleAsync()).MarkersJson);
    }

    [Fact]
    public async Task BrokenPlan_IsRefused_WithItsErrors()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var draftId = await OpenDraftAsync(h, s);

        var attached = await s.Service.AttachPlanAsync(draftId, """{ "schemaVersion": 1, "segments": [] }""");

        Assert.False(attached.Accepted);
        Assert.NotEmpty(attached.Errors);
        Assert.Null((await s.Service.GetDraftAsync(h.WallId))!.Plan);
    }

    [Fact]
    public async Task WallWithoutAPlan_RunsExactlyAsBefore()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);

        var captureId = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var detector = (FakeMarkerDetectionService)s.Detector;
        Assert.All(detector.Calls, o => Assert.Same(MarkerDetectionOptions.Default, o));
        var request = JsonNode.Parse(s.Client.JsonSubmissions.Single(j => j.Kind == "solve").Json)!.AsObject();
        Assert.Equal("segment*6+role", (string?)request["idScheme"]);
        Assert.False(request.ContainsKey("markerSegments"));
        Assert.Null((await s.Service.GetCaptureAsync(captureId))!.PlacementCheck);
        Assert.Null((await s.Service.GetDraftAsync(h.WallId))?.Plan);
    }

    private static async Task<Guid> OpenDraftAsync(WallTestHarness h, CaptureScenario s)
    {
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        return (await s.Service.CreateDraftAsync(h.WallId)).CaptureId;
    }
}
