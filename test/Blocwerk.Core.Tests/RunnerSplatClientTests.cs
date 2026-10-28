// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="RunnerSplatClient"/> on its own: the factory picks it only with the flag, it refuses what it cannot run, it defers
/// a waiting job, it answers unknown ids so the caller submits again, and it reads result files no larger than their entry.
/// </summary>
public class RunnerSplatClientTests
{
    private static readonly ComputeJobPart Photo = ComputeJobPart.File("photos", "p01.jpg", CaptureScenario.TinyJpeg(), "image/jpeg");

    [Fact]
    public async Task TheFactory_PicksTheAdapterOnlyWithTheFlag_AndRunnersOffSwitchesSplatOff()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, RunnerSplatFlowTests.OnRunners());

        Assert.IsType<RunnerSplatClient>(Factory(f, f.Options).Get(ComputeServiceKind.Splat));
        Assert.IsType<ComputeJobClient>(Factory(f, new GpuRunnerOptions()).Get(ComputeServiceKind.Splat));
        Assert.IsType<ComputeJobClient>(Factory(f, f.Options).Get(ComputeServiceKind.Geometry));
        var dead = Factory(f, RunnerSplatFlowTests.OnRunners(GpuRunnerMode.Off)).Get(ComputeServiceKind.Splat);
        Assert.False(dead.IsConfigured);
        Assert.Equal(ComputeFailureKind.NotConfigured, await FailureAsync(() => dead.GetHealthAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task ItOffersPrepareAndFinishOnly_AndAnUnknownIdIsNotFound()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, RunnerSplatFlowTests.OnRunners());
        IComputeJobClient client = Client(f);
        var owner = new ComputeJobOwner(h.WallId, await CaptureAsync(h), null);

        var health = await client.GetHealthAsync(CancellationToken.None);
        Assert.Equal([WallCaptureProcessor.PrepareKind, WallCaptureProcessor.FinishKind], health.Kinds);
        Assert.Contains(MarkerlessCaptureSupport.AnchorsOutput, health.PrepareOutputs);
        Assert.Equal(ComputeFailureKind.NotConfigured, await FailureAsync(() => client.SubmitMultipartAsync("splat", [Photo], owner, CancellationToken.None)));
        foreach (var id in new[] { "job-7", RunnerSplatClient.IdPrefix + Guid.NewGuid() })
        {
            Assert.Equal(ComputeFailureKind.JobNotFound, await FailureAsync(() => client.GetStatusAsync(id, CancellationToken.None)));
        }
    }

    [Fact]
    public async Task AWaitingJob_IsDeferredAfterTheWait_AndItsResultIsServedWithinItsEntrySize()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, RunnerSplatFlowTests.OnRunners());
        IComputeJobClient client = Client(f, TimeSpan.FromMinutes(2));
        var id = await client.SubmitMultipartAsync(
            WallCaptureProcessor.PrepareKind, [Photo, ComputeJobPart.Json("options", "{}")], new ComputeJobOwner(h.WallId, await CaptureAsync(h), null),
            CancellationToken.None);

        Assert.Equal(ComputeJobStates.Queued, (await client.GetStatusAsync(id, CancellationToken.None)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(ComputeFailureKind.Deferred, await FailureAsync(() => client.GetStatusAsync(id, CancellationToken.None)));

        var (runner, _) = await f.AddRunnerAsync("cellar", walls: h.WallId);
        runner = await SplatJobSupport.HelloAsync(f, runner, 16000, SplatJobSupport.PrepareAndFinish);
        var claimed = await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);
        Assert.Equal(id, RunnerSplatClient.IdPrefix + claimed!.Id);
        await f.Queue.AcceptResultAsync(runner, claimed.Id, new MemoryStream(SplatJobSupport.PrepareResult(bundleBytes: 100)), null, null, CancellationToken.None);

        Assert.Equal(ComputeJobStates.Succeeded, (await client.GetStatusAsync(id, CancellationToken.None)).Status);
        Assert.Equal(100, (await client.DownloadFileAsync(id, WallCaptureProcessor.BundleFile, 100, CancellationToken.None)).Length);
        Assert.Equal(ComputeFailureKind.Protocol, await FailureAsync(() => client.DownloadFileAsync(id, WallCaptureProcessor.BundleFile, 99, CancellationToken.None)));
        Assert.Equal(ComputeFailureKind.Protocol, await FailureAsync(() => client.DownloadFileAsync(id, "wall.spz", CancellationToken.None)));

        await client.DoneWithAsync(id, keepResult: true, CancellationToken.None);
        var job = await SplatJobSupport.JobAsync(h, claimed.Id);
        Assert.NotNull(job.InstalledAt);
        var result = f.Files.ResolvePhysicalPath(job.ResultPath!)!;
        Assert.True(File.Exists(result), "the result stays while the capture may read it again");

        await client.DoneWithAsync(id, keepResult: false, CancellationToken.None);
        Assert.Null((await SplatJobSupport.JobAsync(h, claimed.Id)).ResultPath);
        Assert.False(File.Exists(result));
    }

    [Fact]
    public async Task CancellingAWaitingJob_EndsItCancelled()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, RunnerSplatFlowTests.OnRunners());
        IComputeJobClient client = Client(f);
        var id = await client.SubmitMultipartAsync(
            WallCaptureProcessor.FinishKind, [Photo], new ComputeJobOwner(h.WallId, await CaptureAsync(h), null), CancellationToken.None);

        await client.CancelAsync(id, CancellationToken.None);

        var job = await SplatJobSupport.JobAsync(h, Guid.Parse(id[RunnerSplatClient.IdPrefix.Length..]));
        Assert.Equal(GpuJobStatus.Cancelled, job.Status);
        Assert.Equal(ComputeJobStates.Cancelled, (await client.GetStatusAsync(id, CancellationToken.None)).Status);
    }

    private static async Task<ComputeFailureKind> FailureAsync(Func<Task> call) =>
        (await Assert.ThrowsAsync<ComputeJobException>(call)).Kind;

    private static RunnerSplatClient Client(RunnerFixture f, TimeSpan? deferAfter = null) =>
        new(f.Queue, f.Files, NullLogger<RunnerSplatClient>.Instance, deferAfter);

    private static ComputeJobClientFactory Factory(RunnerFixture f, GpuRunnerOptions options)
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var queue = new GpuJobQueue(
            f.Harness.RootContextFactory, f.Files, f.CaptureQueue, options, new GpuJobSignal(), NullLogger<GpuJobQueue>.Instance);
        return new ComputeJobClientFactory(http, new BlocwerkSettings(), NullLoggerFactory.Instance, queue, f.Files);
    }

    private static async Task<Guid> CaptureAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var capture = new WallCapture { WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallCaptureStatus.Solving };
        db.WallCaptures.Add(capture);
        await db.SaveChangesAsync();
        return capture.Id;
    }
}
