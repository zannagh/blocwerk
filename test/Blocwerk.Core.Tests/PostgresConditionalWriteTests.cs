// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The conditional <c>ExecuteUpdate</c>s that several workers race on (the capture's follow-up record and its stage
/// timeline), on a real PostgreSQL with one connection per writer: a write whose condition no longer holds matches no row
/// and reads again, so no writer's change is lost.
/// </summary>
[Trait("Db", "Postgres")]
public class PostgresConditionalWriteTests
{
    [PostgresFact]
    public async Task ManyWritersOfTheFollowUpRecord_AllLand()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var (captureId, modelId) = await CaptureFollowUpChainTests.SeedAsync(h);
        var keys = Enumerable.Range(0, 6).Select(i => $"step{i}").ToList();
        using var start = new ManualResetEventSlim();

        await Task.WhenAll(keys.Select(key => Task.Run(async () =>
        {
            start.Wait();
            var entry = new CaptureFollowUpEntry(key, CaptureFollowUpOutcome.Done, key, DateTimeOffset.UtcNow);
            var stored = await CaptureFollowUpRecordStore.UpdateForModelAsync(h.CreateContext, captureId, modelId, r => r.With(entry), default);
            Assert.NotNull(stored);
        })).Concat([Task.Run(start.Set)]));

        Assert.Equal(keys.Order(), (await RecordAsync(h, captureId)).Steps.Select(s => s.Key).Order());
    }

    [PostgresFact]
    public async Task AWriteBetweenReadAndSave_MatchesNoRow_AndTheSaveMergesWhatWasWritten()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var (captureId, modelId) = await CaptureFollowUpChainTests.SeedAsync(h);
        var other = new CaptureFollowUpEntry("other", CaptureFollowUpOutcome.Done, "by another worker", DateTimeOffset.UtcNow);
        var mine = new CaptureFollowUpEntry("mine", CaptureFollowUpOutcome.Done, "mine", DateTimeOffset.UtcNow);
        var writes = 0;

        var stored = await CaptureFollowUpRecordStore.UpdateForModelAsync(
            h.CreateContext,
            captureId,
            modelId,
            r => r.With(mine),
            default,
            async () =>
            {
                if (writes++ == 0)
                {
                    await CaptureFollowUpRecordStore.UpdateAsync(h.CreateContext, captureId, r => r.With(other), default);
                }
            });

        Assert.Equal(2, writes);
        Assert.Equal(["other", "mine"], stored!.Steps.Select(s => s.Key));
        Assert.Equal(["other", "mine"], (await RecordAsync(h, captureId)).Steps.Select(s => s.Key));
    }

    [PostgresFact]
    public async Task ARepointBetweenReadAndSave_WritesNothing()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var (captureId, modelId) = await CaptureFollowUpChainTests.SeedAsync(h);
        var mine = new CaptureFollowUpEntry("mine", CaptureFollowUpOutcome.Done, "mine", DateTimeOffset.UtcNow);

        var stored = await CaptureFollowUpRecordStore.UpdateForModelAsync(
            h.CreateContext,
            captureId,
            modelId,
            r => r.With(mine),
            default,
            async () =>
            {
                await using var db = h.CreateContext();
                await db.WallCaptures.Where(c => c.Id == captureId).ExecuteUpdateAsync(s => s.SetProperty(c => c.GeometryModelId, (Guid?)null));
            });

        Assert.Null(stored);
        Assert.Empty((await RecordAsync(h, captureId)).Steps);
    }

    [PostgresFact]
    public async Task TheRunningMarkAndTheUpdateTime_AreStampedByTheConditionalWrite()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var before = DateTimeOffset.UtcNow;

        await CaptureFollowUpRecordStore.UpdateAsync(
            h.CreateContext, captureId, r => r.Starting(new CaptureFollowUpRunning("place", "Place", started)), default);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.AsNoTracking().SingleAsync(c => c.Id == captureId);
        Assert.Equal(started.ToUnixTimeMilliseconds(), capture.FollowUpRunningSince!.Value.ToUnixTimeMilliseconds());
        Assert.True(capture.UpdatedAt >= before.AddSeconds(-1));
    }

    [PostgresFact]
    public async Task TwoWritersOfACapturesTimeline_BothLand()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await h.SeedWallAsync(holdCount: 0);
        var id = await CaptureTimelineTests.AddAsync(h, WallCaptureStatus.Splatting);
        await using var worker = h.CreateContext();
        await using var rerender = h.CreateContext();
        var a = await worker.WallCaptures.SingleAsync(c => c.Id == id);
        var b = await rerender.WallCaptures.SingleAsync(c => c.Id == id);

        b.TexturesJobId = CaptureTextureOutcome.RerenderMark;
        await rerender.SaveChangesAsync();
        a.Status = WallCaptureStatus.Succeeded;
        await worker.SaveChangesAsync();

        Assert.Equal(
            [("splatting", (string?)CaptureTimeline.Done), (CaptureTimeline.Rerender, null)],
            (await TimelineAsync(h, id)).Select(e => (e.Stage, e.Outcome)));
    }

    [PostgresFact]
    public async Task ManyWritersOfATimeline_AllLand_AndEachStageIsClosedByItsOwnChange()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await h.SeedWallAsync(holdCount: 0);
        var id = await CaptureTimelineTests.AddAsync(h, WallCaptureStatus.Succeeded);
        using var start = new ManualResetEventSlim();
        var writers = new Action<WallCapture>[]
        {
            c => c.TexturesJobId = CaptureTextureOutcome.RerenderMark,
            c => c.SolveJobId = CaptureResolveMark.Mark,
        };

        await Task.WhenAll(writers.Select(change => Task.Run(async () =>
        {
            start.Wait();
            await CaptureTimelineTests.UpdateAsync(h, id, change);
        })).Concat([Task.Run(start.Set)]));

        Assert.Equal(
            [CaptureTimeline.Rerender, CaptureTimeline.Resolve],
            (await TimelineAsync(h, id)).Select(e => e.Stage).Order());
    }

    [PostgresFact]
    public async Task AStatusWalk_StampsTheTimelineInOrder_WithPostgresTimestamps()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await h.SeedWallAsync(holdCount: 0);
        var id = await CaptureTimelineTests.AddAsync(h, WallCaptureStatus.Queued);

        foreach (var status in new[] { WallCaptureStatus.Detecting, WallCaptureStatus.Solving, WallCaptureStatus.Succeeded })
        {
            await CaptureTimelineTests.UpdateAsync(h, id, c => c.Status = status);
        }

        var timeline = await TimelineAsync(h, id);
        Assert.Equal(["queued", "detecting", "solving"], timeline.Select(e => e.Stage));
        Assert.All(timeline, e => Assert.Equal(CaptureTimeline.Done, e.Outcome));
        Assert.All(timeline.Zip(timeline.Skip(1)), pair => Assert.True(pair.First.EndedAt <= pair.Second.StartedAt));
    }

    private static async Task<List<CaptureTimelineEntry>> TimelineAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return [.. CaptureTimeline.Parse(await db.WallCaptures.Where(c => c.Id == id).Select(c => c.TimelineJson).SingleAsync())];
    }

    private static async Task<CaptureFollowUpRecord> RecordAsync(WallTestHarness h, Guid captureId)
    {
        await using var db = h.CreateContext();
        return CaptureFollowUpRecord.Parse(await db.WallCaptures.Where(c => c.Id == captureId).Select(c => c.FollowUpJson).SingleAsync());
    }
}
