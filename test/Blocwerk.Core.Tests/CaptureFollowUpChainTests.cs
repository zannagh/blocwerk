// <copyright file="CaptureFollowUpChainTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The post-capture chain on its own: steps run in order, each outcome is recorded on the capture, a recorded step
/// never runs twice, a failing step does not stop the next one, a restart mid-step resumes at that step, and the
/// photo-real steps wait for the end of the capture and run again for a new photo-real view.
/// </summary>
public class CaptureFollowUpChainTests
{
    [Fact]
    public async Task Steps_RunInOrder_AndTheCaptureSaysWhatTheyDid()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await SeedAsync(h);
        var log = new List<string>();
        var footprints = new ScriptedFollowUpStep("footprints", 200, log) { Summary = "653 hold shapes refined from several photos" };
        var place = new ScriptedFollowUpStep("place", 100, log) { Summary = "856 holds placed on the 3D model" };
        var volumes = new ScriptedFollowUpStep("volumes", 300, log) { Run = (_, _) => CaptureFollowUpStepResult.Skipped("not yet") };

        var record = await FollowUpChains.Build(h.RootContextFactory, footprints, volumes, place)
            .RunAsync(captureId, CaptureFollowUpPhase.Model, default);

        Assert.Equal(["place", "footprints", "volumes"], log);
        Assert.Equal(["place", "footprints", "volumes"], record.Steps.Select(s => s.Key));
        var stored = CaptureFollowUpRecord.Parse(await FollowUpJsonAsync(h, captureId));
        Assert.Equal(record.Steps.Select(s => (s.Key, s.Outcome)), stored.Steps.Select(s => (s.Key, s.Outcome)));
        Assert.Equal(
            "856 holds placed on the 3D model, 653 hold shapes refined from several photos.",
            CaptureFollowUpText.Summary(stored));
        Assert.Null(CaptureFollowUpText.Note(stored));
    }

    [Fact]
    public async Task RecordedSteps_NeverRunAgain()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await SeedAsync(h);
        var log = new List<string>();
        var chain = FollowUpChains.Build(
            h.RootContextFactory, new ScriptedFollowUpStep("place", 100, log), new ScriptedFollowUpStep("footprints", 200, log));

        await chain.RunAsync(captureId, CaptureFollowUpPhase.Model, default);
        await chain.RunAsync(captureId, CaptureFollowUpPhase.Model, default);
        await chain.RunAsync(captureId, CaptureFollowUpPhase.Final, default);

        Assert.Equal(["place", "footprints"], log);
    }

    [Fact]
    public async Task AFailingStep_IsRecorded_AndTheNextStepsStillRun()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await SeedAsync(h);
        var log = new List<string>();
        var place = new ScriptedFollowUpStep("place", 100, log) { Summary = "3 holds placed on the 3D model" };
        var broken = new ScriptedFollowUpStep("footprints", 200, log) { Run = (_, _) => throw new InvalidOperationException("boom") };
        var last = new ScriptedFollowUpStep("last", 300, log) { Summary = "2 holds measured in the photo-real view" };

        var record = await FollowUpChains.Build(h.RootContextFactory, place, broken, last)
            .RunAsync(captureId, CaptureFollowUpPhase.Model, default);

        Assert.Equal(["place", "footprints", "last"], log);
        Assert.Equal(
            [CaptureFollowUpOutcome.Done, CaptureFollowUpOutcome.Failed, CaptureFollowUpOutcome.Done],
            record.Steps.Select(s => s.Outcome));
        Assert.Equal("3 holds placed on the 3D model, 2 holds measured in the photo-real view.", CaptureFollowUpText.Summary(record));
        Assert.Equal("Step footprints failed.", CaptureFollowUpText.Note(record));
        Assert.DoesNotContain("boom", await FollowUpJsonAsync(h, captureId));
    }

    [Fact]
    public async Task ARestartInTheMiddleOfAStep_ResumesAtThatStep()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await SeedAsync(h);
        var log = new List<string>();
        using var shutdown = new CancellationTokenSource();
        var interrupted = new ScriptedFollowUpStep("footprints", 200, log)
        {
            Run = (_, ct) =>
            {
                shutdown.Cancel();
                ct.ThrowIfCancellationRequested();
                return CaptureFollowUpStepResult.Done("never");
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FollowUpChains.Build(
                h.RootContextFactory, new ScriptedFollowUpStep("place", 100, log), interrupted, new ScriptedFollowUpStep("last", 300, log))
            .RunAsync(captureId, CaptureFollowUpPhase.Model, shutdown.Token));
        Assert.Equal(["place"], CaptureFollowUpRecord.Parse(await FollowUpJsonAsync(h, captureId)).Steps.Select(s => s.Key));

        // A new process: a fresh chain over the same capture row.
        var resumedLog = new List<string>();
        var record = await FollowUpChains.Build(
                h.RootContextFactory,
                new ScriptedFollowUpStep("place", 100, resumedLog),
                new ScriptedFollowUpStep("footprints", 200, resumedLog),
                new ScriptedFollowUpStep("last", 300, resumedLog))
            .RunAsync(captureId, CaptureFollowUpPhase.Model, default);

        Assert.Equal(["footprints", "last"], resumedLog);
        Assert.Equal(["place", "footprints", "last"], record.Steps.Select(s => s.Key));
    }

    [Fact]
    public async Task PhotoRealSteps_WaitForTheEnd_AndRunAgainForANewPhotoRealView()
    {
        using var h = new WallTestHarness();
        var (captureId, modelId) = await SeedAsync(h);
        var log = new List<string>();
        var protrusion = new ScriptedFollowUpStep("protrusion", 300, log, needsPhotoReal: true);
        var chain = FollowUpChains.Build(h.RootContextFactory, new ScriptedFollowUpStep("place", 100, log), protrusion);

        await chain.RunAsync(captureId, CaptureFollowUpPhase.Model, default);
        Assert.Equal(["place"], log);

        await chain.RunAsync(captureId, CaptureFollowUpPhase.Final, default);
        await chain.RunAsync(captureId, CaptureFollowUpPhase.Final, default);
        Assert.Equal(["place", "protrusion"], log);
        Assert.Null(protrusion.Calls.Single().SplatId);

        var splatId = await AddSplatAsync(h, modelId);
        await chain.RunAsync(captureId, CaptureFollowUpPhase.Final, default);

        Assert.Equal(["place", "protrusion", "protrusion"], log);
        Assert.Equal(splatId, protrusion.Calls[1].SplatId);
    }

    [Fact]
    public async Task AModelThatIsNotActive_RunsNothing()
    {
        using var h = new WallTestHarness();
        var (captureId, modelId) = await SeedAsync(h);
        await using (var db = h.CreateContext())
        {
            (await db.WallGeometryModels.SingleAsync(m => m.Id == modelId)).IsActive = false;
            await db.SaveChangesAsync();
        }

        var log = new List<string>();
        await FollowUpChains.Build(h.RootContextFactory, new ScriptedFollowUpStep("place", 100, log))
            .RunAsync(captureId, CaptureFollowUpPhase.Final, default);

        Assert.Empty(log);
        Assert.Null(await FollowUpJsonAsync(h, captureId));
    }

    /// <summary>A wall with an active model and a capture that produced it (still running its last stage).</summary>
    internal static async Task<(Guid CaptureId, Guid ModelId)> SeedAsync(WallTestHarness h)
    {
        await h.SeedWallAsync(holdCount: 0);
        await using var db = h.CreateContext();
        var model = new WallGeometryModel { WallId = h.WallId, Json = GlyphGeometryJson.Build(), SchemaVersion = 1, Source = "test", IsActive = true };
        var capture = new WallCapture
        {
            WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallCaptureStatus.Texturing, GeometryModelId = model.Id,
        };
        db.WallGeometryModels.Add(model);
        db.WallCaptures.Add(capture);
        await db.SaveChangesAsync();
        return (capture.Id, model.Id);
    }

    private static async Task<Guid> AddSplatAsync(WallTestHarness h, Guid modelId)
    {
        await using var db = h.CreateContext();
        var splat = new WallGeometrySplat { GeometryModelId = modelId, StoredPath = "s.spz", SizeBytes = 1, FrameJson = "{}" };
        db.WallGeometrySplats.Add(splat);
        await db.SaveChangesAsync();
        return splat.Id;
    }

    private static async Task<string?> FollowUpJsonAsync(WallTestHarness h, Guid captureId)
    {
        await using var db = h.CreateContext();
        return await db.WallCaptures.Where(c => c.Id == captureId).Select(c => c.FollowUpJson).SingleAsync();
    }
}
