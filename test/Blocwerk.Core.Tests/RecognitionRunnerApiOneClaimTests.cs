// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Text.Json;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Recognition;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.RecognitionApiSupport;

namespace Blocwerk.Core.Tests;

/// <summary>
/// One job per runner across both queues (a held 3D job blocks recognition claims, also with <c>RUNNERS__MODE=off</c>;
/// processes sharing a key do not each take one), and the 3D job calls answering 410 while runners are off.
/// </summary>
public class RecognitionRunnerApiOneClaimTests
{
    private static readonly GpuRunnerOptions RunnersOff = new() { ClaimWait = TimeSpan.Zero, Mode = GpuRunnerMode.Off };

    [Fact]
    public async Task RunnersOff_ARunnerHoldingA3DJob_ClaimsNoRecognition_AndThe3DJobCallsAre410()
    {
        using var h = new WallTestHarness();
        using var r = await RecognitionFixture.CreateAsync(h, Options(RecognitionMode.QueueWithFallback), RunnersOff);
        var key = await OwnedRunnerAsync(r, "splat,markers");
        var gpuJob = await r.Runners.AddJobAsync(h.WallId);
        await HoldGpuJobAsync(r, gpuJob.Id, held: true);
        var job = await r.EnqueueAsync(RecognitionLane.Interactive);
        await using var host = await RunnerApiTestHost.StartAsync(r.Runners, r.Queue);

        Assert.Equal(204, (await ClaimAsync(host, key)).Status);

        var path = $"{Api}/jobs/{gpuJob.Id}";
        await AssertOverAsync(await host.SendAsync(HttpMethod.Get, $"{path}/bundle", key));
        await AssertOverAsync(await host.PostJsonAsync($"{path}/progress", key, """{"fraction":0.5}"""));
        await AssertOverAsync(await host.PostJsonAsync($"{path}/fail", key, """{"reason":"oom","retryable":true}"""));
        await AssertOverAsync(await host.SendAsync(HttpMethod.Put, $"{path}/result", key, new ByteArrayContent([1, 2, 3])));
        await AssertOverAsync(await host.SendAsync(HttpMethod.Put, $"{path}/preview?step=1&total=2", key, new ByteArrayContent([1, 2, 3])));

        // Once the 3D job is no longer held (its lease ran out), the runner gets recognition work again.
        await HoldGpuJobAsync(r, gpuJob.Id, held: false);
        Assert.Equal(job.Id, (await ClaimAsync(host, key)).Claim!.Value.GetProperty("jobId").GetGuid());
    }

    [Fact]
    public async Task ProcessesSharingAKey_RacingBothQueues_GetOneJobBetweenThem()
    {
        using var h = new WallTestHarness();
        using var r = await RecognitionFixture.CreateAsync(h, Options());
        var key = await OwnedRunnerAsync(r, "splat,markers");
        await r.Runners.AddJobAsync(h.WallId);
        await r.EnqueueAsync(RecognitionLane.Interactive);
        await using var host = await RunnerApiTestHost.StartAsync(r.Runners, r.Queue);

        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => ClaimAsync(host, key, $"process-{i}")));

        Assert.Single(claims, c => c.Status == 200);
        Assert.Equal(3, claims.Count(c => c.Status == 204));
    }

    private static async Task AssertOverAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(Blocwerk.Web.Endpoints.RunnerApiEndpoints.GoneOver, body.GetProperty("reason").GetString());
    }

    /// <summary>Marks the 3D job as running on the fixture's owned runner (as claimed before runners were turned off), or queued.</summary>
    private static async Task HoldGpuJobAsync(RecognitionFixture r, Guid jobId, bool held)
    {
        await using var db = r.Harness.CreateContext();
        var runnerId = await db.GpuRunners.Where(x => x.Scope == RunnerScope.Owned).Select(x => x.Id).SingleAsync();
        var now = r.Clock.GetUtcNow();
        await db.GpuJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, held ? GpuJobStatus.Running : GpuJobStatus.Queued)
            .SetProperty(j => j.ClaimedByRunnerId, held ? runnerId : null)
            .SetProperty(j => j.HeartbeatAt, now)
            .SetProperty(j => j.LeaseExpiresAt, held ? now.AddMinutes(5) : null));
    }
}
