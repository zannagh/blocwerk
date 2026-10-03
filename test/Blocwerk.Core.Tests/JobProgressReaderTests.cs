// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The progress read model over captures: the pipeline per stage (remaining time from the stage's median), the texture
/// re-render and re-solve lanes, and the follow-up chain's running and recorded steps.
/// </summary>
public class JobProgressReaderTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ARunningCapture_ShowsItsStage_WithTheRemainingTimeFromTheStagesMedian()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        foreach (var seconds in new[] { 60, 80, 100 })
        {
            var done = await CaptureTimelineTests.AddAsync(h, WallCaptureStatus.Succeeded);
            await SetTimelineAsync(h, done, Entry("solving", Now.AddDays(-2), seconds));
        }

        var id = await CaptureTimelineTests.AddAsync(h, WallCaptureStatus.Solving, c => (c.Progress, c.Stage) = (0.4, "Solving the 3D model: bundle adjustment"));
        await SetTimelineAsync(h, id, Entry("detecting", Now.AddMinutes(-2), 60), new CaptureTimelineEntry("solving", Now.AddSeconds(-30)));

        var job = Assert.Single((await ReadAsync(h)).Jobs, j => j.Kind == JobKinds.Capture);

        Assert.Equal(($"capture:{id}", JobStates.Running, "solving"), (job.Id, job.State, job.Stage));
        Assert.Equal((40, 50.0, JobEtaSources.History), (job.Percent, job.EtaSeconds, job.EtaSource));
        Assert.Equal("Solving the 3D model: bundle adjustment", job.Detail);
        Assert.Equal(["detecting", "solving"], job.Stages!.Select(s => s.Stage));
        Assert.Equal((60.0, 30.0), (job.Stages![0].Seconds, job.Stages[1].Seconds));
        Assert.False(string.IsNullOrEmpty(job.WallName));
    }

    [Fact]
    public async Task WithoutHistory_OrPastTheMedian_TheRemainingTimeIsUnknown()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var id = await CaptureTimelineTests.AddAsync(h, WallCaptureStatus.Texturing);
        await SetTimelineAsync(h, id, new CaptureTimelineEntry("texturing", Now.AddMinutes(-1)));

        var job = Assert.Single((await ReadAsync(h)).Jobs);

        Assert.Equal((null, null), (job.EtaSeconds, job.EtaSource));
    }

    [Fact]
    public async Task EndedCaptures_AreListedWithinTheWindow_FailedOnesWithTheirError()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var failed = await CaptureTimelineTests.AddAsync(h, WallCaptureStatus.Failed, c => (c.Error, c.CompletedAt) = ("No markers found", Now.AddHours(-1)));
        await CaptureTimelineTests.AddAsync(h, WallCaptureStatus.Succeeded, c => c.CompletedAt = Now.AddDays(-3));

        var snapshot = await ReadAsync(h);
        var job = Assert.Single(snapshot.Jobs);

        Assert.Equal(($"capture:{failed}", JobStates.Failed, "No markers found"), (job.Id, job.State, job.LastError));
        Assert.Equal(Now.AddHours(-1), job.EndedAt);
        Assert.Empty((await ReadAsync(h, TimeSpan.Zero)).Jobs);
    }

    [Fact]
    public async Task ManyEndedCaptures_NeverCutOffARunningOne()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var running = await CaptureTimelineTests.AddAsync(h, WallCaptureStatus.Solving, c => c.CreatedAt = Now.AddDays(-3));
        await using (var db = h.CreateContext())
        {
            db.WallCaptures.AddRange(Enumerable.Range(0, JobProgressReader.MaxRows + 10).Select(i => new WallCapture
            {
                WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallCaptureStatus.Succeeded,
                CreatedAt = Now.AddHours(-2), CompletedAt = Now.AddMinutes(-i),
            }));
            await db.SaveChangesAsync();
        }

        var jobs = (await ReadAsync(h)).Jobs;

        Assert.Equal($"capture:{running}", jobs[0].Id);
        Assert.Equal(JobProgressReader.MaxRows, jobs.Count(j => j.State == JobStates.Succeeded));
    }

    [Fact]
    public async Task TheReRenderAndReSolveLanes_AreJobsOfTheirOwn()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var id = await CaptureTimelineTests.AddAsync(h, WallCaptureStatus.Succeeded, c =>
        {
            c.TexturesJobId = CaptureTextureOutcome.RerenderMark + "job-7";
            c.FollowUpJson = (CaptureFollowUpRecord.Empty with { Note = "Solving the 3D model again failed (no markers); the active model stays." }).ToJson();
        });
        await SetTimelineAsync(
            h, id, new CaptureTimelineEntry(CaptureTimeline.Rerender, Now.AddSeconds(-20)),
            new CaptureTimelineEntry(CaptureTimeline.Resolve, Now.AddMinutes(-9), Now.AddMinutes(-5), CaptureTimeline.Failed));

        var jobs = (await ReadAsync(h)).Jobs;

        var rerender = Assert.Single(jobs, j => j.Kind == JobKinds.TextureRerender);
        Assert.Equal((JobStates.Running, Now.AddSeconds(-20), (DateTimeOffset?)null), (rerender.State, rerender.StartedAt, rerender.EndedAt));
        var resolve = Assert.Single(jobs, j => j.Kind == JobKinds.Resolve);
        Assert.Equal((JobStates.Failed, Now.AddMinutes(-5)), (resolve.State, resolve.EndedAt));
        Assert.StartsWith("Solving the 3D model again failed", resolve.LastError);
    }

    [Fact]
    public async Task AMarkFromBeforeTheTimeline_IsRunning_WithAnUnknownStart()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var id = await CaptureTimelineTests.AddAsync(h, WallCaptureStatus.Succeeded);
        await using (var db = h.CreateContext())
        {
            await db.WallCaptures.Where(c => c.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.SolveJobId, CaptureResolveMark.Mark).SetProperty(c => c.TimelineJson, (string?)null));
        }

        var job = Assert.Single((await ReadAsync(h)).Jobs);

        Assert.Equal((JobKinds.Resolve, JobStates.Running, (DateTimeOffset?)null, (double?)null), (job.Kind, job.State, job.StartedAt, job.EtaSeconds));
    }

    [Fact]
    public async Task FollowUps_TheRunningStepWithItsUsualTime_AndTheStepsRecordedInTheWindow()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        foreach (var seconds in new[] { 30, 40, 50 })
        {
            await CaptureTimelineTests.AddAsync(h, WallCaptureStatus.Succeeded, c => c.FollowUpJson = Record(Step("place", Now.AddDays(-1), seconds)).ToJson());
        }

        var record = Record(Step("volumes", Now.AddMinutes(-3), 60), Step("place", Now.AddDays(-2), 30) with { Outcome = CaptureFollowUpOutcome.Failed })
            .Starting(new CaptureFollowUpRunning("links", "Suggesting hold links", Now.AddSeconds(-10)));
        var id = await CaptureTimelineTests.AddAsync(h, WallCaptureStatus.Succeeded, c => c.FollowUpJson = record.ToJson());
        await AddHistoryAsync(h, "links", 25, 25, 25);

        var steps = (await ReadAsync(h)).Jobs.Where(j => j.Kind == JobKinds.FollowUp && j.CaptureId == id).ToList();

        Assert.Equal([$"followUp:{id}:links", $"followUp:{id}:volumes"], steps.Select(s => s.Id));
        Assert.Equal((JobStates.Running, "Suggesting hold links", 15.0, JobEtaSources.History), (steps[0].State, steps[0].Detail, steps[0].EtaSeconds, steps[0].EtaSource));
        Assert.Equal((JobStates.Succeeded, Now.AddMinutes(-3).AddSeconds(-60), Now.AddMinutes(-3)), (steps[1].State, steps[1].StartedAt, steps[1].EndedAt));
    }

    [Fact]
    public async Task TheChain_MarksTheRunningStep_AndRecordsItsStart()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        CaptureFollowUpRunning? seen = null;
        var step = new ScriptedFollowUpStep("place", 100, [])
        {
            Run = (_, _) =>
            {
                using var db = h.CreateContext();
                seen = CaptureFollowUpRecord.Parse(db.WallCaptures.Single(c => c.Id == captureId).FollowUpJson).Running;
                return CaptureFollowUpStepResult.Done("placed");
            },
        };

        await FollowUpChains.Build(h.RootContextFactory, step).RunAsync(captureId, CaptureFollowUpPhase.Model, default);

        await using var check = h.CreateContext();
        var stored = CaptureFollowUpRecord.Parse(check.WallCaptures.Single(c => c.Id == captureId).FollowUpJson);
        Assert.Equal("place", seen?.Key);
        Assert.Null(stored.Running);
        Assert.Equal(seen!.StartedAt, Assert.Single(stored.Steps).StartedAt);
    }

    internal static Task<JobProgressSnapshot> ReadAsync(WallTestHarness h, TimeSpan? recent = null, JobProgressReader? reader = null) =>
        (reader ?? new JobProgressReader(h.RootContextFactory, clock: new MutableTestClock(Now)))
            .ReadAsync(new JobProgressScope(null, recent ?? TimeSpan.FromHours(24)), default);

    private static CaptureTimelineEntry Entry(string stage, DateTimeOffset start, int seconds) =>
        new(stage, start, start.AddSeconds(seconds), CaptureTimeline.Done);

    private static CaptureFollowUpEntry Step(string key, DateTimeOffset at, int seconds) =>
        new(key, CaptureFollowUpOutcome.Done, "ok", at, StartedAt: at.AddSeconds(-seconds));

    private static CaptureFollowUpRecord Record(params CaptureFollowUpEntry[] steps) => new(steps);

    private static async Task AddHistoryAsync(WallTestHarness h, string key, params int[] seconds)
    {
        foreach (var s in seconds)
        {
            await CaptureTimelineTests.AddAsync(h, WallCaptureStatus.Succeeded, c => c.FollowUpJson = Record(Step(key, Now.AddDays(-4), s)).ToJson());
        }
    }

    /// <summary>Overwrites the stamped timeline (no save, so nothing is stamped over it).</summary>
    private static async Task SetTimelineAsync(WallTestHarness h, Guid id, params CaptureTimelineEntry[] entries)
    {
        var json = CaptureTimeline.ToJson([.. entries]);
        await using var db = h.CreateContext();
        await db.WallCaptures.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.TimelineJson, json));
    }
}
