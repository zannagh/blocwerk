// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The follow-up chain's "running step" mark for the progress API: written conditionally (and stamping the row's
/// last-written time), and never left behind: not by a graceful shutdown, not on a capture whose model is no longer active,
/// not by a dead process (the startup sweep).
/// </summary>
public class FollowUpRunningMarkTests
{
    [Fact]
    public async Task TheMark_IsWrittenThroughTheConditionalStore_AndStampsTheRow()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        var before = (await LoadAsync(h, captureId)).UpdatedAt;
        DateTimeOffset? whileRunning = null;
        var step = new ScriptedFollowUpStep("place", 100, [])
        {
            Run = (_, _) =>
            {
                whileRunning = LoadAsync(h, captureId).GetAwaiter().GetResult().UpdatedAt;
                return CaptureFollowUpStepResult.Done("placed");
            },
        };

        await FollowUpChains.Build(h.RootContextFactory, step).RunAsync(captureId, CaptureFollowUpPhase.Model, default);

        Assert.True(whileRunning > before);
        Assert.Null(Record(await LoadAsync(h, captureId)).Running);
    }

    [Fact]
    public async Task AGracefulShutdownInAStep_DropsTheMark()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        using var stop = new CancellationTokenSource();
        var step = new ScriptedFollowUpStep("place", 100, [])
        {
            Run = (_, ct) =>
            {
                stop.Cancel();
                ct.ThrowIfCancellationRequested();
                return CaptureFollowUpStepResult.Done("never");
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FollowUpChains.Build(h.RootContextFactory, step).RunAsync(captureId, CaptureFollowUpPhase.Model, stop.Token));

        var record = Record(await LoadAsync(h, captureId));
        Assert.Null(record.Running);
        Assert.Empty(record.Steps);
    }

    [Fact]
    public async Task AMarkOnACaptureWhoseModelIsNoLongerActive_IsDropped()
    {
        using var h = new WallTestHarness();
        var (captureId, modelId) = await CaptureFollowUpChainTests.SeedAsync(h);
        await SetRecordAsync(h, captureId, CaptureFollowUpRecord.Empty.Starting(new CaptureFollowUpRunning("place", "Placing", DateTimeOffset.UtcNow)));
        await using (var db = h.CreateContext())
        {
            await db.WallGeometryModels.Where(m => m.Id == modelId).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsActive, false));
        }

        await FollowUpChains.Build(h.RootContextFactory).RunAsync(captureId, CaptureFollowUpPhase.Model, default);

        Assert.Null(Record(await LoadAsync(h, captureId)).Running);
    }

    [Fact]
    public async Task TheStartupSweep_DropsOnlyMarksOfAnEarlierProcess()
    {
        using var h = new WallTestHarness();
        var (stale, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        var processStart = DateTimeOffset.UtcNow;
        var fresh = await CaptureTimelineTests.AddAsync(h, WallCaptureStatus.Succeeded);
        await SetRecordAsync(h, stale, CaptureFollowUpRecord.Empty.Starting(new CaptureFollowUpRunning("place", "Placing", processStart.AddHours(-1))));
        await SetRecordAsync(h, fresh, CaptureFollowUpRecord.Empty.Starting(new CaptureFollowUpRunning("links", "Links", processStart.AddSeconds(1))));

        var dropped = await CaptureFollowUpChain.ClearStaleRunningAsync(h.RootContextFactory, processStart, default);

        Assert.Equal(1, dropped);
        Assert.Null(Record(await LoadAsync(h, stale)).Running);
        Assert.Equal("links", Record(await LoadAsync(h, fresh)).Running?.Key);
    }

    [Fact]
    public async Task AnEntryRefusedAfterAModelSwap_DropsItsMarkAnyway()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        var corrected = new WallGeometryModel { WallId = h.WallId, Json = GlyphGeometryJson.Build(), SchemaVersion = 1, Source = "correction" };
        await using (var seed = h.CreateContext())
        {
            seed.WallGeometryModels.Add(corrected);
            await seed.SaveChangesAsync();
        }

        var step = new ScriptedFollowUpStep("place", 100, [])
        {
            Run = (_, _) =>
            {
                // A correction re-points the capture while the step runs; the mark stays in its record.
                using var db = h.CreateContext();
                db.WallCaptures.Where(c => c.Id == captureId).ExecuteUpdate(s => s.SetProperty(c => c.GeometryModelId, corrected.Id));
                return CaptureFollowUpStepResult.Done("placed on the old model");
            },
        };

        await FollowUpChains.Build(h.RootContextFactory, step).RunAsync(captureId, CaptureFollowUpPhase.Model, default);

        var capture = await LoadAsync(h, captureId);
        Assert.Null(Record(capture).Running);
        Assert.Empty(Record(capture).Steps);
        Assert.Null(capture.FollowUpRunningSince);
    }

    private static CaptureFollowUpRecord Record(WallCapture capture) => CaptureFollowUpRecord.Parse(capture.FollowUpJson);

    private static async Task SetRecordAsync(WallTestHarness h, Guid id, CaptureFollowUpRecord record)
    {
        var json = record.ToJson();
        var since = record.Running?.StartedAt;
        await using var db = h.CreateContext();
        await db.WallCaptures.Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.FollowUpJson, json).SetProperty(c => c.FollowUpRunningSince, since));
    }

    private static async Task<WallCapture> LoadAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return await db.WallCaptures.AsNoTracking().SingleAsync(c => c.Id == id);
    }
}
