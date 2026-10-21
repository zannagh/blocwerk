// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Jobs;
using Blocwerk.Core.Pulse;
using Blocwerk.Core.Runners;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.Enhancement;

namespace Blocwerk.Core.Tests.Pulse;

/// <summary>
/// Each migrated job pulses where it moves and never sends a user id: the runner queue's bulk writes, the hold search's
/// in-memory status, and the enhancement's long step. Tracked job rows are the interceptor's (its own tests).
/// </summary>
public sealed class PulseJobEmissionTests
{
    [Fact]
    public async Task ARunnerJob_PulsesItsClaim_ItsHeartbeats_AndItsEnd()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);

        await f.Queue.TryClaimAsync(runner, null, default);
        await f.Queue.ProgressAsync(runner, job.Id, new RunnerProgress(0.25, 7500, 30000, "train", null), default);
        await f.Queue.FailAsync(runner, job.Id, new RunnerFailure("bad bundle", false), default);

        var pulses = f.Pulses.Of(PulseTopics.GpuJob);
        Assert.Equal([(0.0, false), (0.25, false), (0.25, true)], pulses.Select(p => (p.Progress ?? -1, p.Ended)));
        Assert.All(pulses, p => Assert.Equal((h.WallId, job.Id, FeedAudience.Setters), (p.WallId, p.JobId, p.Audience)));
    }

    [Fact]
    public async Task ARetriedRunnerJob_IsNotEnded_SoItsNextClaimPulsesAsUsual()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);

        await f.Queue.TryClaimAsync(runner, null, default);
        await f.Queue.FailAsync(runner, job.Id, new RunnerFailure("out of memory", true), default);

        var last = f.Pulses.Of(PulseTopics.GpuJob)[^1];
        Assert.Equal((0.0, false), (last.Progress, last.Ended)); // back in the queue, progress reset
    }

    [Fact]
    public async Task AHoldSearch_PulsesQueued_EachPhoto_AndItsEnd()
    {
        var pulses = new RecordingWallPulse();
        using var jobs = new HoldSearchJobs(pulse: pulses);
        var wallId = Guid.NewGuid();

        Assert.True(jobs.TryStart(wallId, (progress, _) =>
        {
            progress.Report(new HoldSearchProgress(1, 2));
            progress.Report(new HoldSearchProgress(2, 2));
            return Task.FromResult(new HoldProposalRunResult(2, 3, 1, 1, 0, "test"));
        }, out _));
        await CircuitCacheFixture.WaitUntilAsync(() => pulses.Published.Any(p => p.Ended));

        Assert.Equal([(-1.0, false), (0.5, false), (1.0, false), (1.0, true)], pulses.Published.Select(p => (p.Progress ?? -1, p.Ended)));
        Assert.All(pulses.Published, p => Assert.Equal((wallId, PulseTopics.HoldSearch, wallId, FeedAudience.Setters), (p.WallId, p.Topic, p.JobId, p.Audience)));
        Assert.Equal(JobStates.Succeeded, jobs.Get(wallId)!.State);
    }

    [Fact]
    public async Task AFailedHoldSearch_EndsItsPulses_Too()
    {
        var pulses = new RecordingWallPulse();
        using var jobs = new HoldSearchJobs(pulse: pulses);
        var wallId = Guid.NewGuid();

        jobs.TryStart(wallId, (_, _) => throw new UserFacingException("no photos"), out _);
        await CircuitCacheFixture.WaitUntilAsync(() => pulses.Published.Any(p => p.Ended));

        Assert.Equal(JobStates.Failed, jobs.Get(wallId)!.State);
    }

    [Fact]
    public async Task AnEnhancementStepsProgress_PulsesTheRun_PhotoByPhoto()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var step = new FakeEnhancementStep("new-holds", 700, WallEnhancementItemKind.NewHold, (ctx, _) =>
        {
            ctx.Progress!.Report(new WallEnhancementProgress(1, 4));
            ctx.Progress.Report(new WallEnhancementProgress(3, 4));
            return Task.FromResult(WallEnhancementStepOutcome.Done("searched"));
        });

        var view = await f.StartAndWaitAsync(f.Service(step));

        var steps = f.Pulses.Of(PulseTopics.Enhancement);
        Assert.Equal([0.25, 0.75], steps.Select(p => p.Progress ?? -1));
        Assert.All(steps, p => Assert.Equal((f.WallId, view.RunId!.Value, FeedAudience.Setters), (p.WallId, p.JobId, p.Audience)));
    }
}
