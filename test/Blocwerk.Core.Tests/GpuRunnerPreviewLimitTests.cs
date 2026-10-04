// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A runner picks its preview steps and total itself, so the server bounds them: a count per job, a step gap, a floor on
/// the step, a ceiling on the total, a rate limit and a size cap. Refusals are 422 (<see cref="RunnerJobOutcome.PreviewRefused"/>),
/// which the runner skips without failing the job.
/// </summary>
public class GpuRunnerPreviewLimitTests
{
    [Fact]
    public async Task OnlyAFewPreviewsPerJobAreTaken()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);

        foreach (var step in new[] { 4000, 8000, 12000, 16000 })
        {
            Assert.Equal(RunnerJobOutcome.Ok, await f.PreviewAsync(step));
        }

        Assert.Equal(RunnerJobOutcome.PreviewRefused, await f.PreviewAsync(20000));
        var job = await JobAsync(h);
        Assert.Equal(4, job.PreviewCount);
        Assert.Equal(16000, job.PreviewStep);
        Assert.Equal(GpuJobStatus.Claimed, job.Status); // a refused preview never touches the job
    }

    [Fact]
    public async Task APreviewTooCloseToTheLastOne_OrTooEarly_OrWithAnAbsurdTotal_IsRefused()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);

        Assert.Equal(RunnerJobOutcome.PreviewRefused, await f.PreviewAsync(2000));
        Assert.Equal(RunnerJobOutcome.PreviewRefused, await f.PreviewAsync(7000, total: int.MaxValue));
        Assert.Equal(RunnerJobOutcome.Ok, await f.PreviewAsync(7000));
        Assert.Equal(RunnerJobOutcome.PreviewRefused, await f.PreviewAsync(8000));
        Assert.Equal(RunnerJobOutcome.Ok, await f.PreviewAsync(9000));
        Assert.Equal(2, (await JobAsync(h)).PreviewCount);
    }

    [Fact]
    public async Task PreviewsAreRateLimitedPerJob()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerCaptureFlow.StartAsync(h);

        Assert.Equal(RunnerJobOutcome.Ok, await f.PreviewAsync(7000));
        Assert.Equal(RunnerJobOutcome.PreviewRefused, await f.PreviewAsync(20000, wait: TimeSpan.FromSeconds(10)));
        Assert.Equal(RunnerJobOutcome.Ok, await f.PreviewAsync(20000, wait: TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public async Task APreviewOverTheSizeCap_IsTooLarge_AndNotCounted()
    {
        using var h = new WallTestHarness();
        var options = new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, Mode = GpuRunnerMode.Always, MaxPreviewBytes = 64 };
        using var f = await RunnerCaptureFlow.StartAsync(h, options);

        Assert.Equal(RunnerJobOutcome.TooLarge, await f.PreviewAsync(7000));
        var job = await JobAsync(h);
        Assert.Equal(0, job.PreviewCount);
        Assert.Null(job.PreviewPath);
    }

    [Fact]
    public void TheLimitsAreConfigurable()
    {
        var job = new GpuJob { BundlePath = "b", BundleSha256 = "s", PreparedPath = "p", Status = GpuJobStatus.Running, PreviewCount = 1, PreviewStep = 5000 };
        Assert.False(GpuJobPreviews.MayAccept(job, 9000, 50000, new GpuRunnerOptions { MaxPreviewsPerJob = 1 }));
        Assert.True(GpuJobPreviews.MayAccept(job, 9000, 50000, new GpuRunnerOptions { MaxPreviewsPerJob = 2 }));
    }

    private static async Task<GpuJob> JobAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.GpuJobs.AsNoTracking().OrderByDescending(j => j.CreatedAt).FirstAsync();
    }
}
