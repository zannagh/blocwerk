// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Recognition;
using Blocwerk.Core.Runners;
using static Blocwerk.Core.Tests.RecognitionApiSupport;

namespace Blocwerk.Core.Tests;

/// <summary>
/// When the runner API is mapped (<c>RUNNERS__MODE</c> and <c>RECOGNITION__MODE</c>), the model download by hash, a
/// restarted runner getting its recognition claim back, and the run deadline of a claim.
/// </summary>
public class RecognitionRunnerApiModeTests
{
    private static readonly GpuRunnerOptions RunnersOff = new() { ClaimWait = TimeSpan.Zero, Mode = GpuRunnerMode.Off };

    [Fact]
    public async Task RunnersOff_AndRecognitionInProcess_LeaveTheApiUnmapped()
    {
        using var h = new WallTestHarness();
        using var r = await RecognitionFixture.CreateAsync(h, Options(RecognitionMode.InProcess), RunnersOff);
        await RegisterInstanceAsync(r, r.Options);
        await using var host = await RunnerApiTestHost.StartAsync(r.Runners, r.Queue);

        Assert.Equal(HttpStatusCode.NotFound, (await host.PostJsonAsync($"{Api}/hello", InstanceKey, "{}")).StatusCode);
    }

    [Fact]
    public async Task RunnersOff_WithQueuedRecognition_ServesOnlyRecognitionKinds()
    {
        using var h = new WallTestHarness();
        using var r = await RecognitionFixture.CreateAsync(h, Options(RecognitionMode.QueueWithFallback), RunnersOff);
        var key = await OwnedRunnerAsync(r, "splat,markers");
        await r.Runners.AddJobAsync(h.WallId);
        var job = await r.EnqueueAsync(RecognitionLane.Interactive);
        await using var host = await RunnerApiTestHost.StartAsync(r.Runners, r.Queue);

        var (_, claim) = await ClaimAsync(host, key);
        Assert.Equal(job.Id, claim!.Value.GetProperty("jobId").GetGuid());
        await host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{job.Id}/result", key, Succeeded());
        Assert.Equal(204, (await ClaimAsync(host, key)).Status);
    }

    [Fact]
    public async Task Models_AreServedByHash_WithRange_OnlyFromTheModelsDirectory()
    {
        var dir = Directory.CreateTempSubdirectory("blocwerk-models-").FullName;
        try
        {
            var model = Encoding.UTF8.GetBytes("not really an onnx model, but bytes all the same");
            await File.WriteAllBytesAsync(Path.Combine(dir, "holds.onnx"), model);
            var sha = Convert.ToHexStringLower(SHA256.HashData(model));
            using var h = new WallTestHarness();
            using var r = await RecognitionFixture.CreateAsync(h, Options(modelsDirectory: dir));
            await RegisterInstanceAsync(r, r.Options);
            await using var host = await RunnerApiTestHost.StartAsync(r.Runners, r.Queue);

            var whole = await host.SendAsync(HttpMethod.Get, $"{Api}/models/{sha}", InstanceKey);
            Assert.Equal(model, await whole.Content.ReadAsByteArrayAsync());
            using var ranged = Request(HttpMethod.Get, $"{Api}/models/{sha}", InstanceKey, null);
            ranged.Headers.Range = new RangeHeaderValue(4, null);
            var tail = await host.Client.SendAsync(ranged);
            Assert.Equal(HttpStatusCode.PartialContent, tail.StatusCode);
            Assert.Equal(model[4..], await tail.Content.ReadAsByteArrayAsync());

            Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Get, $"{Api}/models/{sha}", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, $"{Api}/models/{new string('0', 64)}", InstanceKey)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, $"{Api}/models/..%2Fholds.onnx", InstanceKey)).StatusCode);

            // A file changed after it was hashed is not served under the old hash.
            await File.WriteAllBytesAsync(Path.Combine(dir, "holds.onnx"), [.. model, 1]);
            File.SetLastWriteTimeUtc(Path.Combine(dir, "holds.onnx"), DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, $"{Api}/models/{sha}", InstanceKey)).StatusCode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RestartedRunner_GetsItsSilentClaimBack_AndTheOldProcessIsRefused()
    {
        using var h = new WallTestHarness();
        using var r = await RecognitionFixture.CreateAsync(h, Options());
        var key = await OwnedRunnerAsync(r, "markers");
        var job = await r.EnqueueAsync(RecognitionLane.Interactive);
        await r.EnqueueAsync(RecognitionLane.Interactive);
        await using var host = await RunnerApiTestHost.StartAsync(r.Runners, r.Queue);
        Assert.Equal(job.Id, (await ClaimAsync(host, key, "old")).Claim!.Value.GetProperty("jobId").GetGuid());

        r.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(204, (await ClaimAsync(host, key, "new")).Status);

        r.Clock.Advance(TimeSpan.FromSeconds(12));
        var (_, back) = await ClaimAsync(host, key, "new");
        Assert.Equal(job.Id, back!.Value.GetProperty("jobId").GetGuid());
        Assert.True(back.Value.GetProperty("reattached").GetBoolean());

        using var stale = Request(HttpMethod.Post, $"{Api}/jobs/{job.Id}/progress", key, "old", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.SendAsync(stale)).StatusCode);
        using var fresh = Request(HttpMethod.Post, $"{Api}/jobs/{job.Id}/progress", key, "new", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(fresh)).StatusCode);
    }

    [Fact]
    public async Task AClaim_PastItsRunDeadline_GoesBack_EvenWithHeartbeats()
    {
        using var h = new WallTestHarness();
        using var r = await RecognitionFixture.CreateAsync(h, Options());
        var runner = await r.RunnerAsync("pc");
        var job = await r.EnqueueAsync(RecognitionLane.Interactive);
        Assert.Equal(job.Id, (await r.Queue.TryClaimAsync(runner, CancellationToken.None))?.Id);
        for (var i = 0; i < 3; i++)
        {
            r.Clock.Advance(TimeSpan.FromSeconds(20));
            Assert.Equal(RunnerJobOutcome.Ok, await r.Queue.ProgressAsync(runner, job.Id, new RunnerProgress(0.5, null, null, null, null), CancellationToken.None));
        }

        r.Clock.Advance(TimeSpan.FromSeconds(5));
        await r.Queue.SweepAsync(CancellationToken.None);

        var row = await r.RowAsync(job.Id);
        Assert.Equal(GpuJobStatus.Queued, row.Status);
        Assert.Contains("longer than 60 s", row.Error);
        Assert.Contains(runner.Id.ToString(), row.FailedRunnerIdsJson);
    }

    [Fact]
    public void RunCaps_ArePerKindAndLane()
    {
        var options = new RecognitionQueueOptions();

        Assert.Equal(TimeSpan.FromSeconds(270), options.RunOf(RecognitionKinds.DetectHolds, RecognitionLane.Interactive, 3));
        Assert.Equal(TimeSpan.FromSeconds(60), options.RunOf(RecognitionKinds.Presence, RecognitionLane.Interactive, 0));
        Assert.Equal(TimeSpan.FromMinutes(30), options.RunOf(RecognitionKinds.DetectHolds, RecognitionLane.Background, 8));
        Assert.Equal(RecognitionMode.QueueWithFallback, RecognitionQueueOptions.ParseMode(" Queue+Fallback "));
        Assert.Equal(RecognitionMode.InProcess, RecognitionQueueOptions.ParseMode(null));
    }
}
