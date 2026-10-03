// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A capture's stage timeline is stamped by every save from what it changed: the pipeline status, a texture re-render and
/// a re-solve, each with its start, end and outcome, and the row's last-written time.
/// </summary>
public class CaptureTimelineTests
{
    [Fact]
    public async Task StatusChanges_CloseTheStage_AndOpenTheNext()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var id = await AddAsync(h, WallCaptureStatus.Queued);

        foreach (var status in new[] { WallCaptureStatus.Detecting, WallCaptureStatus.Solving, WallCaptureStatus.Texturing, WallCaptureStatus.Splatting })
        {
            await UpdateAsync(h, id, c => c.Status = status);
        }

        await UpdateAsync(h, id, c => c.Status = WallCaptureStatus.SucceededWithoutSplat);

        var timeline = CaptureTimeline.Parse((await LoadAsync(h, id)).TimelineJson);
        Assert.Equal(["queued", "detecting", "solving", "texturing", "splatting"], timeline.Select(e => e.Stage));
        Assert.All(timeline, e => Assert.NotNull(e.EndedAt));
        Assert.Equal(
            [CaptureTimeline.Done, CaptureTimeline.Done, CaptureTimeline.Done, CaptureTimeline.Done, CaptureTimeline.Failed],
            timeline.Select(e => e.Outcome));
        Assert.All(timeline.Zip(timeline.Skip(1)), pair => Assert.True(pair.First.EndedAt <= pair.Second.StartedAt));
    }

    [Fact]
    public async Task AFailedCapture_FailsItsStage_AndOtherWritesOnlyStampTheRow()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var id = await AddAsync(h, WallCaptureStatus.Solving);
        var before = (await LoadAsync(h, id)).UpdatedAt;

        await UpdateAsync(h, id, c => c.Progress = 0.5);
        var progressed = await LoadAsync(h, id);
        await UpdateAsync(h, id, c => c.Status = WallCaptureStatus.Failed);

        Assert.NotNull(before);
        Assert.True(progressed.UpdatedAt >= before);
        Assert.Single(CaptureTimeline.Parse(progressed.TimelineJson));
        var solving = Assert.Single(CaptureTimeline.Parse((await LoadAsync(h, id)).TimelineJson));
        Assert.Equal(("solving", CaptureTimeline.Failed), (solving.Stage, solving.Outcome));
    }

    [Fact]
    public async Task ReRenderAndReSolve_AreTheirOwnLanes_BesideTheFinishedPipeline()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var id = await AddAsync(h, WallCaptureStatus.Succeeded);

        await UpdateAsync(h, id, c => c.TexturesJobId = CaptureTextureOutcome.RerenderMark);
        await UpdateAsync(h, id, c => c.TexturesJobId = CaptureTextureOutcome.RerenderMark + "job-1");
        await UpdateAsync(h, id, c => c.SolveJobId = CaptureResolveMark.Mark);
        var running = CaptureTimeline.Parse((await LoadAsync(h, id)).TimelineJson);
        await UpdateAsync(h, id, c => c.TexturesJobId = "job-1");
        await UpdateAsync(h, id, c => c.SolveJobId = null);

        Assert.Equal([CaptureTimeline.Rerender, CaptureTimeline.Resolve], running.Select(e => e.Stage));
        Assert.All(running, e => Assert.Null(e.EndedAt));
        var ended = CaptureTimeline.Parse((await LoadAsync(h, id)).TimelineJson);
        Assert.Equal(
            [(CaptureTimeline.Rerender, CaptureTimeline.Done), (CaptureTimeline.Resolve, CaptureTimeline.Failed)],
            ended.Select(e => (e.Stage, e.Outcome!)));
    }

    [Fact]
    public async Task TwoWritersOfTheSameCapture_BothLandOnTheTimeline()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var id = await AddAsync(h, WallCaptureStatus.Splatting);
        await using var worker = h.CreateContext();
        await using var rerender = h.CreateContext();
        var a = await worker.WallCaptures.SingleAsync(c => c.Id == id);
        var b = await rerender.WallCaptures.SingleAsync(c => c.Id == id);

        b.TexturesJobId = CaptureTextureOutcome.RerenderMark;
        await rerender.SaveChangesAsync();
        a.Status = WallCaptureStatus.Succeeded;
        await worker.SaveChangesAsync();

        var timeline = CaptureTimeline.Parse((await LoadAsync(h, id)).TimelineJson);
        Assert.Equal(
            [("splatting", (string?)CaptureTimeline.Done), (CaptureTimeline.Rerender, null)],
            timeline.Select(e => (e.Stage, e.Outcome)));
    }

    [Fact]
    public async Task AFailedSave_RecordsNothing_AndItsRetryRecordsOnce()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var id = await AddAsync(h, WallCaptureStatus.Queued);
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == id);
        capture.Status = WallCaptureStatus.Detecting;
        var orphan = db.WallMembers.Add(new WallMember { WallId = Guid.NewGuid(), UserId = Guid.NewGuid(), Role = Enums.WallRole.Member });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(["queued"], CaptureTimeline.Parse((await LoadAsync(h, id)).TimelineJson).Select(e => e.Stage));
        orphan.State = EntityState.Detached;
        await db.SaveChangesAsync();

        Assert.Equal(["queued", "detecting"], CaptureTimeline.Parse((await LoadAsync(h, id)).TimelineJson).Select(e => e.Stage));
    }

    [Fact]
    public void TheTimeline_KeepsTheNewestEntriesOnly()
    {
        var entries = Enumerable.Range(0, CaptureTimeline.MaxEntries + 5)
            .Select(i => new CaptureTimelineEntry("solving", DateTimeOffset.UnixEpoch.AddMinutes(i), DateTimeOffset.UnixEpoch.AddMinutes(i + 1), "done"))
            .ToList();

        var kept = CaptureTimeline.Parse(CaptureTimeline.ToJson(entries));

        Assert.Equal(CaptureTimeline.MaxEntries, kept.Count);
        Assert.Equal(entries[^1], kept[^1]);
        Assert.Empty(CaptureTimeline.Parse("not json"));
    }

    internal static async Task<Guid> AddAsync(WallTestHarness h, WallCaptureStatus status, Action<WallCapture>? change = null)
    {
        await using var db = h.CreateContext();
        var capture = new WallCapture { WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = status };
        change?.Invoke(capture);
        db.WallCaptures.Add(capture);
        await db.SaveChangesAsync();
        return capture.Id;
    }

    internal static async Task UpdateAsync(WallTestHarness h, Guid id, Action<WallCapture> change)
    {
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == id);
        change(capture);
        await db.SaveChangesAsync();
    }

    private static async Task<WallCapture> LoadAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return await db.WallCaptures.AsNoTracking().SingleAsync(c => c.Id == id);
    }
}
