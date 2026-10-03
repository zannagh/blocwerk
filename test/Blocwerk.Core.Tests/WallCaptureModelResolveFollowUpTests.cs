// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Tests.MarkerPlanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static Blocwerk.Core.Tests.MarkerRevisions.RegistrationFixtures;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A re-solve re-derives every follow-up for the new model, also after a restart in the middle of them, and solves with
/// the wall's newer plan revision when that still defines the markers the photos show.
/// </summary>
public class WallCaptureModelResolveFollowUpTests
{
    private static readonly int[] Detected = [0, 1, 2, 6, 7, 12];

    [Fact]
    public async Task AnAdoptedReSolveInterruptedAfterPlacingHolds_RunsTheOtherStepsOnRestart_AndClearsTheMark()
    {
        using var h = new WallTestHarness();
        var log = new List<string>();
        using var s = Scenario(h, log);
        var captureId = await FinishedAsync(s);
        var placed = new CaptureFollowUpEntry(PlaceHoldsFollowUpStep.StepKey, CaptureFollowUpOutcome.Done, "placed", DateTimeOffset.UtcNow);
        await SetRecordAsync(h, (CaptureFollowUpRecord.Empty with { Rederive = true }).With(placed));
        log.Clear();

        using var worker = new WallModelResolveWorker(s.ResolveQueue, s.Processor, h.RootContextFactory, NullLogger<WallModelResolveWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        var record = await WaitAsync(h, r => !r.Rederive);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(["volumes", "measure"], log);
        Assert.Equal([PlaceHoldsFollowUpStep.StepKey, "volumes", "measure"], record.Steps.Select(e => e.Key));
    }

    [Fact]
    public async Task AnUnmarkedRecord_IsNotCompletedByTheReSolvePath()
    {
        using var h = new WallTestHarness();
        var log = new List<string>();
        using var s = Scenario(h, log);
        var captureId = await FinishedAsync(s);
        await SetRecordAsync(h, CaptureFollowUpRecord.Empty);
        log.Clear();

        await s.Processor.ResolveModelAsync(captureId, CancellationToken.None);

        Assert.Empty(log);
    }

    [Fact]
    public async Task Resolve_UsesTheWallsNewerPlanRevision_WhenItStillDefinesTheDetectedMarkers()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.Client.GeometryJson = Solved(Rev1Json);
        var captureId = await FinishedAsync(s, beforeStart: _ => RevisionOneAsync(h, s));
        var moved = await SaveRevisionAsync(h, s, rev1 => rev1.Markers.First(m => !Detected.Contains(m.Id)).Id, move: true);

        Assert.Empty(await s.Service.ResolveModelAsync(captureId));
        await s.Processor.ResolveModelAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.AsNoTracking().SingleAsync(c => c.Id == captureId);
        var active = await db.WallGeometryModels.SingleAsync(m => m.WallId == h.WallId && m.IsActive);
        Assert.Equal(active.Id, capture.GeometryModelId);
        Assert.Equal((2, 2), (capture.PlanRevision!.Value, active.PlanRevision!.Value));
        Assert.Equal(moved.XMm, MarkerPlanJson.FromJson(capture.PlanJson!, out _)!.Markers.Single(m => m.Id == moved.Id).XMm);
        Assert.Contains("current plan revision 2", (await s.Service.GetCaptureAsync(captureId))!.FollowUpNote);
    }

    [Fact]
    public async Task Resolve_KeepsTheCapturesPlan_WhenTheNewerRevisionNoLongerDefinesADetectedMarker()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.Client.GeometryJson = Solved(Rev1Json);
        var captureId = await FinishedAsync(s, beforeStart: _ => RevisionOneAsync(h, s));
        await SaveRevisionAsync(h, s, _ => 12, move: false);

        Assert.Empty(await s.Service.ResolveModelAsync(captureId));
        await s.Processor.ResolveModelAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.AsNoTracking().SingleAsync(c => c.Id == captureId);
        Assert.Equal(1, capture.PlanRevision);
        Assert.Contains("no longer defines marker(s) 12", (await s.Service.GetCaptureAsync(captureId))!.FollowUpNote);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThePlacementCheck_IsStoredOnlyWithTheActivation(bool registers)
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.Client.GeometryJson = Solved(Rev1Json);
        var captureId = await FinishedAsync(s, beforeStart: _ => RevisionOneAsync(h, s));
        await using (var db = h.CreateContext())
        {
            (await db.WallCaptures.SingleAsync(c => c.Id == captureId)).PlacementCheckJson = "{\"before\":true}";
            await db.SaveChangesAsync();
        }

        if (!registers)
        {
            s.Client.GeometryJson = CaptureScenario.GeometryWithCameras("p00", "p01");
        }

        Assert.Empty(await s.Service.ResolveModelAsync(captureId));
        await s.Processor.ResolveModelAsync(captureId, CancellationToken.None);

        await using var read = h.CreateContext();
        var capture = await read.WallCaptures.AsNoTracking().SingleAsync(c => c.Id == captureId);
        var activated = await read.WallGeometryModels.AnyAsync(m => m.Id == capture.GeometryModelId && m.Source == WallCaptureProcessor.ResolvedModelSource(captureId));
        Assert.Equal(registers, activated);
        Assert.Equal(registers, capture.PlacementCheckJson != "{\"before\":true}");
        Assert.NotNull(capture.PlacementCheckJson);
    }

    private static CaptureScenario Scenario(WallTestHarness h, List<string> log) => new(h, followUps: harness => FollowUpChains.Build(
        harness.RootContextFactory,
        new ScriptedFollowUpStep(PlaceHoldsFollowUpStep.StepKey, 100, log),
        new ScriptedFollowUpStep("volumes", 250, log),
        new ScriptedFollowUpStep("measure", 300, log, needsPhotoReal: true)));

    private static async Task<Guid> FinishedAsync(CaptureScenario s, Func<Guid, Task>? beforeStart = null)
    {
        var captureId = await s.StartCaptureAsync(beforeStart: beforeStart);
        Assert.Equal(captureId, await s.Queue.DequeueAsync(CancellationToken.None));
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        return captureId;
    }

    /// <summary>The legacy model, then revision 1 of the plan built from it (the capture pins it).</summary>
    private static async Task RevisionOneAsync(WallTestHarness h, CaptureScenario s)
    {
        var imported = await WallGlyphSettingsTests.Service(h).ImportGeometryAsync(h.WallId, Rev1Json, "legacy");
        Assert.True(imported.Succeeded, string.Join(" ", imported.Errors));
        var rev1 = await s.MarkerPlans.BuildFromMeasuredGeometryAsync(h.WallId, AtticMarkerPlan.Photo);
        Assert.Equal(1, (await s.MarkerPlans.SavePlanAsync(h.WallId, rev1!)).Revision);
    }

    /// <summary>Saves revision 2: the marker <paramref name="pick"/> chooses moved 50 mm, or re-issued under the unused id 49.</summary>
    private static async Task<PlanMarker> SaveRevisionAsync(WallTestHarness h, CaptureScenario s, Func<MarkerPlan, int> pick, bool move)
    {
        await using var db = h.CreateContext();
        var json = await db.WallMarkerPlans.Where(p => p.WallId == h.WallId && p.IsCurrent).Select(p => p.Json).SingleAsync();
        var rev1 = MarkerPlanJson.FromJson(json, out _)!;
        var id = pick(rev1);
        var target = rev1.Markers.Single(m => m.Id == id) with { XMm = rev1.Markers.Single(m => m.Id == id).XMm + 50 };
        var replaced = move ? target : rev1.Markers.Single(m => m.Id == id) with { Id = 49 };
        var markers = rev1.Markers.Select(m => m.Id == id ? replaced : m).ToList();
        Assert.Equal(2, (await s.MarkerPlans.SavePlanAsync(h.WallId, rev1 with { Markers = markers })).Revision);
        return target;
    }

    private static async Task SetRecordAsync(WallTestHarness h, CaptureFollowUpRecord record)
    {
        await using var db = h.CreateContext();
        (await db.WallCaptures.SingleAsync()).FollowUpJson = record.ToJson();
        await db.SaveChangesAsync();
    }

    private static async Task<CaptureFollowUpRecord> WaitAsync(WallTestHarness h, Func<CaptureFollowUpRecord, bool> done)
    {
        for (var i = 0; i < 200; i++)
        {
            await using var db = h.CreateContext();
            var record = CaptureFollowUpRecord.Parse((await db.WallCaptures.AsNoTracking().SingleAsync()).FollowUpJson);
            if (done(record))
            {
                return record;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The re-solve's follow-ups were not resumed.");
    }

    private static string Solved(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var cameras = root["cameras"]!.AsArray();
        for (var i = 0; i < cameras.Count; i++)
        {
            cameras[i]!["image"] = $"p{i:D2}";
        }

        return root.ToJsonString();
    }
}
