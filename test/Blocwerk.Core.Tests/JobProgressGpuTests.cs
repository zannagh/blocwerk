// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Jobs;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The progress read model over GPU jobs: training with the step rate of the current claim and the trainer's numbers, the
/// history of the quality's trainings where no step is reported, and the server's finish as a job of its own.
/// </summary>
public class JobProgressGpuTests
{
    [Fact]
    public async Task ATrainingJob_ReportsSteps_TheRateOfItsClaim_AndTheTrainersNumbers()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, default);

        await f.Queue.ProgressAsync(runner, job.Id, Report(1000, "step 1000/30000 splats 120000 loss 0.0412"), default);
        f.Clock.Advance(TimeSpan.FromSeconds(100));
        await f.Queue.ProgressAsync(runner, job.Id, Report(2000, "step 2000/30000 splats 150000 loss 0.03"), default);

        var item = Assert.Single(await ReadAsync(f), j => j.Kind == JobKinds.GpuTraining);
        Assert.Equal((JobStates.Running, "training", 2000, 30000), (item.State, item.Stage, item.Step, item.TotalSteps));
        Assert.Equal((2800.0, JobEtaSources.Rate), (item.EtaSeconds, item.EtaSource));
        Assert.Equal((0.03, 150000, 10.0), (item.Training!.Loss, item.Training.Splats, item.Training.StepsPerSecond));
        Assert.Equal(("gpu", f.Clock.GetUtcNow()), (item.RunnerName, item.UpdatedAt));
    }

    [Fact]
    public void TheRateAnchor_MovesWithANewClaim_OrATrainingThatStartedOver()
    {
        var claimed = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var job = new GpuJob
        {
            BundlePath = "b", BundleSha256 = "s", PreparedPath = "p", ClaimedAt = claimed, StepAnchor = 5000, StepAnchorAt = claimed.AddMinutes(1),
        };

        Assert.Equal((5000, claimed.AddMinutes(1)), Anchor(GpuJobProgressFacts.From(job, Report(6000), claimed.AddMinutes(2))));
        Assert.Equal((100, claimed.AddMinutes(2)), Anchor(GpuJobProgressFacts.From(job, Report(100), claimed.AddMinutes(2))));
        job.ClaimedAt = claimed.AddMinutes(5);
        Assert.Equal((7000, claimed.AddMinutes(6)), Anchor(GpuJobProgressFacts.From(job, Report(7000), claimed.AddMinutes(6))));

        // Within the claim, a report without a step or numbers keeps the stored ones.
        (job.Step, job.StepAnchor, job.StepAnchorAt, job.Loss, job.SplatCount) = (7000, 7000, claimed.AddMinutes(6), 0.05, 900000);
        var none = GpuJobProgressFacts.From(job, Report(null, "uploading"), claimed.AddMinutes(7));
        Assert.Equal((7000, 0.05, 900000), (none.Step, none.Loss, none.Splats));

        // A new claim does not carry the previous claim's step, loss or splat count until it reports its own.
        job.ClaimedAt = claimed.AddMinutes(8);
        var reclaimed = GpuJobProgressFacts.From(job, Report(null, "downloading"), claimed.AddMinutes(9));
        Assert.Equal(((int?)null, (double?)null, (int?)null), (reclaimed.Step, reclaimed.Loss, reclaimed.Splats));
    }

    [Theory]
    [InlineData("step 750/15000 splats 55966 loss 0.1416", 0.1416, 55966)]
    [InlineData("step 750/15000 splats 55966 loss 0.1416; resumed from step 500", 0.1416, 55966)]
    [InlineData("step 750/15000 splats 2000000 loss 1.5e-3", 0.0015, 2000000)]
    [InlineData("step 750/15000 splats 1,234 loss 0,1416", null, null)]
    [InlineData("step 750/15000 splats 1.2M loss 0.14x", null, null)]
    [InlineData("step 750/15000", null, null)]
    public void TheTrainersNumbers_AreReadInvariantly_AndUnknownFormsAreNoValue(string detail, double? loss, int? splats)
    {
        Assert.Equal((loss, splats), (GpuJobProgressFacts.ParseLoss(detail), GpuJobProgressFacts.ParseSplats(detail)));
    }

    [Fact]
    public async Task ARealTrainerLine_EndToEnd_StoresStepLossAndSplats()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, default);

        // As the runner sends it: the gsplat trainer's line (see the splat worker's gsplat-train.log fixture) as the detail.
        var line = "step 750/15000 splats 55966 loss 0.1416";
        await f.Queue.ProgressAsync(runner, job.Id, new RunnerProgress(0.05, 750, 15000, "train", line), default);

        await using var db = h.CreateContext();
        var row = await db.GpuJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Equal((750, 15000, 0.1416, 55966), (row.Step, row.TotalSteps, row.Loss, row.SplatCount));
        Assert.Contains("step 750/15000", row.Stage);
    }

    [Fact]
    public async Task APausedRunner_IsShown_OnItsRunningJob_AndOnJobsWaitingOnlyForPausedRunners()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var running = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, default);
        var waiting = await f.AddJobAsync(h.WallId);
        await using (var db = h.CreateContext())
        {
            await db.GpuRunners.Where(r => r.Id == runner.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Paused, true));
        }

        var reader = new JobProgressReader(h.RootContextFactory, clock: f.Clock, runnerQueue: f.Queue);
        var jobs = (await reader.ReadAsync(new JobProgressScope(null, TimeSpan.FromHours(1)), default)).Jobs;

        Assert.True(Assert.Single(jobs, j => j.GpuJobId == running.Id && j.Kind == JobKinds.GpuTraining).RunnerPaused);
        Assert.True(Assert.Single(jobs, j => j.GpuJobId == waiting.Id).RunnerPaused);
    }

    [Fact]
    public async Task WithoutSteps_TheTrainingFallsBackToTheQualitysUsualDuration()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        foreach (var minutes in new[] { 8, 10, 12 })
        {
            var done = await f.AddJobAsync(h.WallId);
            await SetAsync(h, done.Id, GpuJobStatus.Succeeded, claimed: f.Clock.GetUtcNow().AddHours(-3), completed: f.Clock.GetUtcNow().AddHours(-3).AddMinutes(minutes));
        }

        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, default);
        await f.Queue.ProgressAsync(runner, job.Id, new RunnerProgress(0.1, null, null, "train", null), default);
        f.Clock.Advance(TimeSpan.FromMinutes(4));

        var item = Assert.Single(await ReadAsync(f), j => j.Kind == JobKinds.GpuTraining && j.State == JobStates.Running);

        Assert.Equal((360.0, JobEtaSources.History), (item.EtaSeconds, item.EtaSource));
    }

    [Fact]
    public async Task AQueuedJob_HasNoRunnerAndNoEta_AndADeliveredOneIsFinishing()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var queued = await f.AddJobAsync(h.WallId);
        var now = f.Clock.GetUtcNow();
        foreach (var minutes in new[] { 4, 5, 6 })
        {
            var installed = await f.AddJobAsync(h.WallId);
            await SetAsync(h, installed.Id, GpuJobStatus.Succeeded, claimed: now.AddDays(-2), completed: now.AddDays(-2), installed: now.AddDays(-2).AddMinutes(minutes));
        }

        var delivered = await f.AddJobAsync(h.WallId);
        await SetAsync(h, delivered.Id, GpuJobStatus.Succeeded, claimed: now.AddHours(-1), completed: f.Clock.GetUtcNow().AddMinutes(-2));

        var jobs = await ReadAsync(f);

        var wait = Assert.Single(jobs, j => j.Id == $"gpuTraining:{queued.Id}");
        Assert.Equal((JobStates.Queued, "queued", (string?)null, (double?)null), (wait.State, wait.Stage, wait.RunnerName, wait.EtaSeconds));
        var finish = Assert.Single(jobs, j => j.Id == $"finish:{delivered.Id}");
        Assert.Equal((JobStates.Running, "finishing", 180.0, JobEtaSources.History), (finish.State, finish.Stage, finish.EtaSeconds, finish.EtaSource));
        Assert.Equal(JobStates.Succeeded, Assert.Single(jobs, j => j.Id == $"gpuTraining:{delivered.Id}").State);
    }

    private static RunnerProgress Report(int? step, string? detail = null) =>
        new(step / 30000.0, step, step is null ? null : 30000, "train", detail);

    private static (int? Step, DateTimeOffset? At) Anchor(GpuJobProgressFacts facts) => (facts.Anchor, facts.AnchorAt);

    private static async Task<IReadOnlyList<JobProgressItem>> ReadAsync(RunnerFixture f) =>
        (await new JobProgressReader(f.Harness.RootContextFactory, clock: f.Clock).ReadAsync(new JobProgressScope(null, TimeSpan.FromHours(24)), default)).Jobs;

    private static async Task SetAsync(
        WallTestHarness h, Guid id, GpuJobStatus status, DateTimeOffset claimed, DateTimeOffset completed, DateTimeOffset? installed = null)
    {
        await using var db = h.CreateContext();
        await db.GpuJobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, status)
            .SetProperty(j => j.ClaimedAt, claimed)
            .SetProperty(j => j.CompletedAt, completed)
            .SetProperty(j => j.InstalledAt, installed)
            .SetProperty(j => j.ResultPath, "result.spz"));
    }
}
