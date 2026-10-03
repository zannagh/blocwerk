// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Corrections;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Several workers write a capture's follow-up record: a chain run merges its entries into the record as stored now,
/// stops once the capture was re-pointed at another model, and the marks that resume work after a restart (re-derive,
/// run again after a correction) are recovered and cleared.
/// </summary>
public class CaptureFollowUpConcurrencyTests
{
    [Fact]
    public async Task AChainRun_KeepsWhatAnotherWorkerWroteMeanwhile()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        var log = new List<string>();
        var other = new CaptureFollowUpEntry("other", CaptureFollowUpOutcome.Done, "by another worker", DateTimeOffset.UtcNow);
        var place = new ScriptedFollowUpStep("place", 100, log)
        {
            Run = (_, _) =>
            {
                Write(h, captureId, json => (CaptureFollowUpRecord.Parse(json) with { Rederive = true }).With(other).ToJson());
                return CaptureFollowUpStepResult.Done("placed");
            },
        };

        await FollowUpChains.Build(h.RootContextFactory, place, new ScriptedFollowUpStep("footprints", 200, log))
            .RunAsync(captureId, CaptureFollowUpPhase.Model, default);

        var stored = await RecordAsync(h, captureId);
        Assert.True(stored.Rederive);
        Assert.Equal(["other", "place", "footprints"], stored.Steps.Select(s => s.Key));
    }

    [Fact]
    public async Task AChainRun_StopsAndWritesNothing_OnceTheCaptureWasRepointed()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        var log = new List<string>();
        var fresh = CaptureFollowUpRecord.Repointed(null);
        var place = new ScriptedFollowUpStep("place", 100, log)
        {
            Run = (_, _) =>
            {
                Repoint(h, captureId, fresh.ToJson());
                return CaptureFollowUpStepResult.Done("placed on the old model");
            },
        };

        await FollowUpChains.Build(h.RootContextFactory, place, new ScriptedFollowUpStep("footprints", 200, log))
            .RunAsync(captureId, CaptureFollowUpPhase.Model, default);

        Assert.Equal(["place"], log);
        var stored = await RecordAsync(h, captureId);
        Assert.Empty(stored.Steps);
        Assert.True(stored.RunAgain);
    }

    [Fact]
    public async Task ARederiveMark_IsCleared_WhenTheCapturesModelIsNoLongerActive()
    {
        using var h = new WallTestHarness();
        var (captureId, modelId) = await CaptureFollowUpChainTests.SeedAsync(h);
        Write(h, captureId, _ => (CaptureFollowUpRecord.Empty with { Rederive = true }).ToJson());
        await using (var db = h.CreateContext())
        {
            (await db.WallGeometryModels.SingleAsync(m => m.Id == modelId)).IsActive = false;
            await db.SaveChangesAsync();
        }

        var log = new List<string>();
        await FollowUpChains.Build(h.RootContextFactory, new ScriptedFollowUpStep("place", 100, log)).RunMissingAsync(captureId, default);

        Assert.Empty(log);
        Assert.False((await RecordAsync(h, captureId)).Rederive);
    }

    [Fact]
    public async Task ACorrectionsChainLeftByAPreviousProcess_IsQueuedOnStart_RunsAndClearsItsMark()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        Write(h, captureId, _ => CaptureFollowUpRecord.Repointed(null).ToJson());
        var log = new List<string>();
        var queue = new CorrectionFollowUpQueue();
        var chain = FollowUpChains.Build(h.RootContextFactory, new ScriptedFollowUpStep("place", 100, log));
        var worker = new CorrectionFollowUpWorker(queue, chain, h.RootContextFactory, NullLogger<CorrectionFollowUpWorker>.Instance);

        await worker.RecoverAsync(default);
        Assert.Equal(captureId, await queue.DequeueAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));
        await worker.RunAsync(captureId, default);

        Assert.Equal(["place"], log);
        var stored = await RecordAsync(h, captureId);
        Assert.False(stored.RunAgain);
        Assert.Equal(["place"], stored.Steps.Select(s => s.Key));
    }

    [Fact]
    public async Task ACorrection_MarksTheCapturesRecordToRunAgain()
    {
        using var h = new WallTestHarness();
        var (_, captureId) = await GeometryCorrectionFixture.SeedAsync(h);

        var queue = new CorrectionFollowUpQueue();
        await GeometryCorrectionFixture.Service(h, queue)
            .MakeSizesExactAsync(h.WallId, new CaptureScaleReference(1, [1000, 750], [1600, 750], 1980));

        Assert.Equal(captureId, await queue.DequeueAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));
        Assert.True((await RecordAsync(h, captureId)).RunAgain);
    }

    private static void Write(WallTestHarness h, Guid captureId, Func<string?, string> change)
    {
        using var db = h.CreateContext();
        var capture = db.WallCaptures.Single(c => c.Id == captureId);
        capture.FollowUpJson = change(capture.FollowUpJson);
        db.SaveChanges();
    }

    /// <summary>What a correction does to the capture: a new active model, the capture pointed at it with a fresh record.</summary>
    private static void Repoint(WallTestHarness h, Guid captureId, string record)
    {
        using var db = h.CreateContext();
        var capture = db.WallCaptures.Single(c => c.Id == captureId);
        foreach (var active in db.WallGeometryModels.Where(m => m.WallId == capture.WallId && m.IsActive).ToList())
        {
            active.IsActive = false;
        }

        db.SaveChanges();
        var model = new WallGeometryModel { WallId = capture.WallId, Json = GlyphGeometryJson.Build(), SchemaVersion = 1, Source = "correction", IsActive = true };
        db.WallGeometryModels.Add(model);
        capture.GeometryModelId = model.Id;
        capture.FollowUpJson = record;
        db.SaveChanges();
    }

    private static async Task<CaptureFollowUpRecord> RecordAsync(WallTestHarness h, Guid captureId)
    {
        await using var db = h.CreateContext();
        return CaptureFollowUpRecord.Parse(await db.WallCaptures.Where(c => c.Id == captureId).Select(c => c.FollowUpJson).SingleAsync());
    }
}
