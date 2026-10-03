// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.MarkerRevisions.RegistrationFixtures;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A re-solved model goes live together with the capture row, only while the model it was registered to is still the
/// active one, and a re-solve a restart left after storing its model resumes from that model instead of solving again.
/// </summary>
public class WallCaptureModelResolveRecoveryTests
{
    [Fact]
    public async Task AStoredReSolveLeftByARestart_IsActivatedWithoutSolvingAgain()
    {
        using var h = new WallTestHarness();
        var log = new List<string>();
        using var s = new CaptureScenario(h, followUps: harness => FollowUpChains.Build(
            harness.RootContextFactory, new ScriptedFollowUpStep(PlaceHoldsFollowUpStep.StepKey, 100, log)));
        s.Client.GeometryJson = Solved(Rev1Json);
        var captureId = await FinishedAsync(s);
        var activeId = await ActiveIdAsync(h);
        var storedId = await StoreResolvedAsync(h, captureId, referenceModelId: activeId, jobId: "job-left");
        var solves = Solves(s);
        log.Clear();

        await s.Processor.ResolveModelAsync(captureId, CancellationToken.None);

        Assert.Equal(solves, Solves(s));
        Assert.Contains(PlaceHoldsFollowUpStep.StepKey, log);
        await using var db = h.CreateContext();
        Assert.Equal(storedId, (await db.WallGeometryModels.SingleAsync(m => m.WallId == h.WallId && m.IsActive)).Id);
        var capture = await db.WallCaptures.AsNoTracking().SingleAsync(c => c.Id == captureId);
        Assert.Equal(storedId, capture.GeometryModelId);
        Assert.Equal("job-left", capture.SolveJobId);
        Assert.False(CaptureFollowUpRecord.Parse(capture.FollowUpJson).Rederive);
        Assert.Equal(1, await db.WallGeometryModels.CountAsync(m => m.Source == WallCaptureProcessor.ResolvedModelSource(captureId)));
    }

    [Fact]
    public async Task AReSolve_IsNotActivated_WhenTheModelItWasRegisteredToIsNoLongerActive()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.Client.GeometryJson = Solved(Rev1Json);
        var captureId = await FinishedAsync(s);
        var activeId = await ActiveIdAsync(h);
        var storedId = await StoreResolvedAsync(h, captureId, referenceModelId: Guid.NewGuid(), jobId: "job-moved");

        await s.Processor.ResolveModelAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        Assert.Equal(activeId, (await db.WallGeometryModels.SingleAsync(m => m.WallId == h.WallId && m.IsActive)).Id);
        Assert.False((await db.WallGeometryModels.SingleAsync(m => m.Id == storedId)).IsActive);
        var capture = await db.WallCaptures.AsNoTracking().SingleAsync(c => c.Id == captureId);
        Assert.Equal(activeId, capture.GeometryModelId);
        Assert.False(CaptureResolveMark.IsResolving(capture.SolveJobId));
        Assert.False(CaptureTextureOutcome.IsRerendering(capture.TexturesJobId));
        Assert.Contains("active model changed meanwhile", (await s.Service.GetCaptureAsync(captureId))!.FollowUpNote);
        Assert.Empty(await db.WallGeometrySplats.Where(v => v.GeometryModelId == storedId).ToListAsync());
    }

    /// <summary>
    /// What a re-solve stores before a restart: an inactive copy of the active model's document, registered to
    /// <paramref name="referenceModelId"/>, tagged with the job; the capture still carries the re-solve mark with that job.
    /// </summary>
    private static async Task<Guid> StoreResolvedAsync(WallTestHarness h, Guid captureId, Guid referenceModelId, string jobId)
    {
        await using var db = h.CreateContext();
        var active = await db.WallGeometryModels.AsNoTracking().SingleAsync(m => m.WallId == h.WallId && m.IsActive);
        var json = JsonNode.Parse(active.Json)!.AsObject();
        var quality = json["quality"] as JsonObject ?? new JsonObject();
        json["quality"] = quality;
        quality["registration"] = new JsonObject { ["referenceModelId"] = referenceModelId.ToString() };
        var stored = new WallGeometryModel
        {
            WallId = h.WallId,
            Json = json.ToJsonString(),
            SchemaVersion = active.SchemaVersion,
            Source = WallCaptureProcessor.ResolvedModelSource(captureId),
            Notes = WallCaptureProcessor.ResolvedModelNotes(captureId, jobId),
            CreatedByUserId = h.Owner.Id,
            IsActive = false,
            PlanRevision = active.PlanRevision,
        };
        db.WallGeometryModels.Add(stored);
        (await db.WallCaptures.SingleAsync(c => c.Id == captureId)).SolveJobId = CaptureResolveMark.Mark + jobId;
        await db.SaveChangesAsync();
        return stored.Id;
    }

    /// <summary>Solve jobs submitted so far.</summary>
    private static int Solves(CaptureScenario s) =>
        s.Client.JsonSubmissions.Count(j => j.Kind == "solve") + s.Client.MultipartSubmissions.Count(j => j.Kind == "solve");

    private static async Task<Guid> FinishedAsync(CaptureScenario s)
    {
        var captureId = await s.StartCaptureAsync();
        Assert.Equal(captureId, await s.Queue.DequeueAsync(CancellationToken.None));
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        return captureId;
    }

    private static async Task<Guid> ActiveIdAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.WallGeometryModels.Where(m => m.WallId == h.WallId && m.IsActive).Select(m => m.Id).SingleAsync();
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
