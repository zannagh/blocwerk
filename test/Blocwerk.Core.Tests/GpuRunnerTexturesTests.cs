// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Jobs;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Textures jobs in the runner queue: only runners that advertise <c>textures</c> (with the memory) are offered one, old runners
/// never are, the bundle and the result keep to their contract, and the splat side of the queue ignores them.
/// </summary>
public class GpuRunnerTexturesTests
{
    [Fact]
    public void Capabilities_AreStoredKnownOnly_AndAnOldRunnerIsASplatRunner()
    {
        Assert.Equal("splat,textures", RunnerCapabilities.Store(["Splat", "textures", "bogus", "splat"]));
        Assert.Null(RunnerCapabilities.Store(null));
        Assert.Null(RunnerCapabilities.Store(["bogus"]));
        Assert.True(RunnerCapabilities.Allows(null, GpuJobKind.Splat));
        Assert.False(RunnerCapabilities.Allows(null, GpuJobKind.Textures));
        Assert.False(RunnerCapabilities.Allows("splat", GpuJobKind.Textures));
        Assert.True(RunnerCapabilities.Allows("textures", GpuJobKind.Textures));
        Assert.False(RunnerCapabilities.Allows("textures", GpuJobKind.Splat));
    }

    [Fact]
    public async Task Hello_StoresTheCapabilitiesAndMemory_AndAHelloWithoutThemClearsThem()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("mac", walls: h.WallId);

        await f.Queue.HelloAsync(runner, TexturesJobSupport.Hello(24000), CancellationToken.None);
        await using (var db = h.CreateContext())
        {
            var row = await db.GpuRunners.SingleAsync();
            Assert.Equal(("splat,textures", 24000), (row.Capabilities, row.TexturesMemoryMb));
        }

        // The memory means nothing without the capability.
        await f.Queue.HelloAsync(runner, TexturesJobSupport.Hello(24000, RunnerCapabilities.Splat), CancellationToken.None);
        await f.Queue.HelloAsync(runner, TexturesJobSupport.Hello(24000) with { Capabilities = null }, CancellationToken.None);
        await using var again = h.CreateContext();
        var old = await again.GpuRunners.SingleAsync();
        Assert.Equal(((string?)null, (int?)null), (old.Capabilities, old.TexturesMemoryMb));
    }

    [Fact]
    public async Task ARunnerWithoutTheCapability_NeverClaimsATexturesJob_ButStillTrainsSplats()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (old, _) = await f.AddRunnerAsync("old", walls: h.WallId);
        var textures = await TexturesJobSupport.AddTexturesJobAsync(f, h.WallId);

        Assert.Null(await f.Queue.TryClaimAsync(old, null, CancellationToken.None));

        var splat = await f.AddJobAsync(h.WallId);
        Assert.Equal(splat.Id, (await f.Queue.TryClaimAsync(old, null, CancellationToken.None))?.Id);
        Assert.Equal(GpuJobStatus.Queued, (await TexturesJobSupport.JobAsync(h, textures.Id)).Status);
    }

    [Fact]
    public async Task ARunnerWithTheCapability_NeedsTheMemoryTheJobRequires()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("mac", walls: h.WallId);
        await f.Queue.HelloAsync(runner, TexturesJobSupport.Hello(8000), CancellationToken.None);
        var job = await TexturesJobSupport.AddTexturesJobAsync(f, h.WallId, requiredMb: 12000);

        Assert.Null(await f.Queue.TryClaimAsync(runner, null, CancellationToken.None));

        await f.Queue.HelloAsync(runner, TexturesJobSupport.Hello(48000), CancellationToken.None);
        var claimed = await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);
        Assert.Equal((job.Id, GpuJobKind.Textures), (claimed?.Id, claimed?.Kind));
    }

    [Fact]
    public async Task TheClaimAnswer_NamesTheKind_AndOffersNoPreviews()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("mac", walls: h.WallId);
        await f.Queue.HelloAsync(runner, TexturesJobSupport.Hello(), CancellationToken.None);
        var textures = await TexturesJobSupport.AddTexturesJobAsync(f, h.WallId);

        var claim = await f.Queue.ClaimAsync(runner, TimeSpan.Zero, null, CancellationToken.None);

        Assert.Equal((textures.Id, "textures", false, "draft"), (claim?.JobId, claim?.Kind, claim?.Previews, claim?.Quality));
        Assert.Equal(textures.BundleSha256, claim!.BundleSha256);
        var (outcome, path) = await f.Queue.BundleForRunnerAsync(runner, textures.Id, CancellationToken.None);
        Assert.Equal(RunnerJobOutcome.Ok, outcome);
        using var zip = ZipFile.OpenRead(path!);
        Assert.Equal(
            new[] { "geometry.json", "photos/photo_0000.jpg", "textures-job.json" }, zip.Entries.Select(e => e.FullName).Order().ToArray());
    }

    [Fact]
    public async Task ATexturesRunner_ThatSaysOnlyTextures_IsNotOfferedSplats()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("mac", walls: h.WallId);
        await f.Queue.HelloAsync(runner, TexturesJobSupport.Hello(20000, RunnerCapabilities.Textures), CancellationToken.None);
        await f.AddJobAsync(h.WallId);

        Assert.Null(await f.Queue.TryClaimAsync(runner, null, CancellationToken.None));
    }

    [Fact]
    public async Task TheResult_IsAZipOfTheServicesFiles_AndAnythingElseIsRefused()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("mac", walls: h.WallId);
        await f.Queue.HelloAsync(runner, TexturesJobSupport.Hello(), CancellationToken.None);
        var job = await TexturesJobSupport.AddTexturesJobAsync(f, h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);

        byte[][] refused =
        [
            RunnerFixture.SlimPly(10),
            TexturesJobSupport.ResultZip(withFiles: false),
            TexturesJobSupport.ResultZip(true, ("../evil.jpg", [1])),
            TexturesJobSupport.ResultZip(true, ("notes.txt", [1])),
        ];
        foreach (var body in refused)
        {
            Assert.Equal(RunnerJobOutcome.Invalid, await f.Queue.AcceptResultAsync(runner, job.Id, new MemoryStream(body), null, null, CancellationToken.None));
        }

        Assert.Equal(GpuJobStatus.Claimed, (await TexturesJobSupport.JobAsync(h, job.Id)).Status);
        Assert.Equal(
            RunnerJobOutcome.Ok,
            await f.Queue.AcceptResultAsync(runner, job.Id, new MemoryStream(TexturesJobSupport.ResultZip()), null, null, CancellationToken.None));
        var done = await TexturesJobSupport.JobAsync(h, job.Id);
        Assert.Equal((GpuJobStatus.Succeeded, "zip", null), (done.Status, done.ResultFormat, done.InstalledAt));
    }

    [Fact]
    public async Task ASplatResultIsRefusedForATexturesJob_AndPreviewsToo()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("mac", walls: h.WallId);
        await f.Queue.HelloAsync(runner, TexturesJobSupport.Hello(), CancellationToken.None);
        var job = await TexturesJobSupport.AddTexturesJobAsync(f, h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);

        var preview = await f.Queue.AcceptPreviewAsync(runner, job.Id, 7000, 30000, new MemoryStream(RunnerFixture.SlimPly(5)), null, CancellationToken.None);

        Assert.Equal(RunnerJobOutcome.PreviewRefused, preview);
    }

    [Fact]
    public async Task ProgressText_SaysRendering_AndTheProgressApiShowsTheKind()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("mac", walls: h.WallId);
        await f.Queue.HelloAsync(runner, TexturesJobSupport.Hello(), CancellationToken.None);
        var job = await TexturesJobSupport.AddTexturesJobAsync(f, h.WallId);
        var splat = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);

        await f.Queue.ProgressAsync(runner, job.Id, new RunnerProgress(0.4, null, null, "textures", "blending"), CancellationToken.None);

        var jobs = (await new JobProgressReader(h.RootContextFactory, clock: f.Clock)
            .ReadAsync(new JobProgressScope(null, TimeSpan.FromHours(24)), CancellationToken.None)).Jobs;
        var item = Assert.Single(jobs, j => j.GpuJobId == job.Id);
        Assert.Equal((JobKinds.GpuTextures, $"{JobKinds.GpuTextures}:{job.Id}", JobStates.Running, "rendering"), (item.Kind, item.Id, item.State, item.Stage));
        Assert.Contains("rendering the wall textures", item.Detail);
        Assert.Null(item.Training);
        Assert.Equal(JobKinds.GpuTraining, Assert.Single(jobs, j => j.GpuJobId == splat.Id).Kind);
    }

    [Fact]
    public async Task TheSplatSideOfTheQueue_IgnoresATexturesJob()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var job = await TexturesJobSupport.AddTexturesJobAsync(f, h.WallId);

        Assert.Null(await f.Queue.LatestForCaptureAsync(job.CaptureId, CancellationToken.None));
        Assert.False(await f.Queue.CancelForCaptureAsync(job.CaptureId, "retrain", CancellationToken.None));
        Assert.Equal(GpuJobStatus.Queued, (await TexturesJobSupport.JobAsync(h, job.Id)).Status);
    }

    [Fact]
    public async Task AFailedTexturesJob_DoesNotMarkTheCaptureWithoutAPhotoRealView()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("mac", walls: h.WallId);
        await f.Queue.HelloAsync(runner, TexturesJobSupport.Hello(), CancellationToken.None);
        var job = await TexturesJobSupport.AddTexturesJobAsync(f, h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);

        await f.Queue.FailAsync(runner, job.Id, new RunnerFailure("out of memory", Retryable: false), CancellationToken.None);

        Assert.Equal(GpuJobStatus.Failed, (await TexturesJobSupport.JobAsync(h, job.Id)).Status);
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal((WallCaptureStatus.Succeeded, "Done", (string?)null), (capture.Status, capture.Stage, capture.Error));
    }
}
