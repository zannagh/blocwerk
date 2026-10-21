// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Pulse;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Pulse;

/// <summary>The pulse interceptor's cases, run on SQLite and on Postgres: what pulses, and only once it committed.</summary>
internal static class WallPulseInterceptorScenarios
{
    public static async Task ACaptureSave_PulsesAfterItIsSaved(WallTestHarness h)
    {
        var pulses = new RecordingWallPulse();
        await using var db = Context(h, pulses);
        var capture = NewCapture(h);
        db.WallCaptures.Add(capture);
        await db.SaveChangesAsync();
        capture.Progress = 0.4;
        capture.Stage = "Solving";
        await db.SaveChangesAsync();

        Assert.Equal(
            [(PulseTopics.Capture, capture.Id, 0.0), (PulseTopics.Capture, capture.Id, 0.4)],
            pulses.Published.Select(p => (p.Topic, p.JobId, p.Progress ?? -1)));
        Assert.All(pulses.Published, p => Assert.Equal((h.WallId, FeedAudience.Setters), (p.WallId, p.Audience)));
    }

    public static async Task InATransaction_OnlyTheCommitPulses_AndARollbackNothing(WallTestHarness h)
    {
        var pulses = new RecordingWallPulse();
        await using var db = Context(h, pulses);
        var capture = NewCapture(h);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            db.WallCaptures.Add(capture);
            await db.SaveChangesAsync();
            capture.Progress = 0.2;
            await db.SaveChangesAsync();
            Assert.Empty(pulses.Published);
            await tx.CommitAsync();
        }

        // One pulse per job for the whole transaction: the throttle would collapse the rest anyway.
        Assert.Equal(0.2, Assert.Single(pulses.Published).Progress);

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            capture.Progress = 0.9;
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        Assert.Single(pulses.Published);
    }

    public static async Task EveryJobRow_PulsesItsTopic_AndOtherRowsNothing(WallTestHarness h)
    {
        var pulses = new RecordingWallPulse();
        await using var db = Context(h, pulses);
        var capture = NewCapture(h);
        db.WallCaptures.Add(capture);
        await db.SaveChangesAsync();
        var before = pulses.Published.Count;

        var job = new GpuJob { WallId = h.WallId, CaptureId = capture.Id, BundlePath = "b", BundleSha256 = "s", PreparedPath = "p", Status = GpuJobStatus.Succeeded, Progress = 1 };
        var run = new WallEnhancementRun { WallId = h.WallId, Generation = 0, CreatedByUserId = h.Owner.Id };
        var refresh = new WallRefresh { WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallRefreshStatus.Running };
        var session = new WallUpdateSession { WallId = h.WallId, StagedGeneration = 1, ShapeTotal = 4, ShapeDone = 1 };
        db.AddRange(job, run, refresh, session);
        await db.SaveChangesAsync();

        var added = pulses.Published.Skip(before).ToDictionary(p => p.Topic);
        Assert.Equal((job.Id, 1.0, true), (added[PulseTopics.GpuJob].JobId, added[PulseTopics.GpuJob].Progress, added[PulseTopics.GpuJob].Ended));
        Assert.Equal(run.Id, added[PulseTopics.Enhancement].JobId);
        Assert.Equal(refresh.Id, added[PulseTopics.Refresh].JobId);
        Assert.Equal((session.Id, 0.25), (added[PulseTopics.Update].JobId, added[PulseTopics.Update].Progress));
        Assert.Equal(4, added.Count);

        var count = pulses.Published.Count;
        var hold = await db.Holds.FirstAsync();
        hold.X += 0.01;
        await db.SaveChangesAsync();
        Assert.Equal(count, pulses.Published.Count);
    }

    public static async Task TheFollowUpRecordsBulkWrite_PulsesTheCapture(WallTestHarness h)
    {
        var pulses = new RecordingWallPulse();
        var capture = NewCapture(h);
        await using (var db = h.CreateContext())
        {
            db.WallCaptures.Add(capture);
            await db.SaveChangesAsync();
        }

        await CaptureFollowUpRecordStore.UpdateAsync(() => Context(h, pulses), capture.Id, _ => CaptureFollowUpRecord.Carried(Guid.NewGuid()), default);

        var pulse = Assert.Single(pulses.Published);
        Assert.Equal((h.WallId, PulseTopics.Capture, capture.Id), (pulse.WallId, pulse.Topic, pulse.JobId));
    }

    private static WallCapture NewCapture(WallTestHarness h) =>
        new() { WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallCaptureStatus.Queued };

    private static BlocwerkDbContext Context(WallTestHarness h, IWallPulse pulses) =>
        TestDb.Create(h.DbContextFactory.ConnectionString, new WallPulseInterceptor(pulses));
}
