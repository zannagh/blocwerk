// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The hold proposal search's detection on a 3D runner: it is only queued when a runner that finds holds is online, an
/// older runner is never offered it, a job nobody claims in time is cancelled (the app detects), and a delivered result
/// comes back as the holds per photo with the job closed and its files gone.
/// </summary>
public sealed class RunnerHoldDetectionTests : IDisposable
{
    private const string Cameras =
        "{\"cameras\":[" +
        "{\"image\":\"p00\",\"width\":64,\"height\":48,\"K\":[1,0,0,0,1,0,0,0,1],\"R\":[1,0,0,0,1,0,0,0,1],\"t\":[0,0,0]}," +
        "{\"image\":\"p01\",\"width\":64,\"height\":48,\"K\":[1,0,0,0,1,0,0,0,1],\"R\":[1,0,0,0,1,0,0,0,1],\"t\":[0,0,0]}]}";

    private readonly string modelFile = Path.Combine(Path.GetTempPath(), $"holds-model-{Guid.NewGuid():N}.onnx");

    public RunnerHoldDetectionTests() => File.WriteAllBytes(modelFile, [1, 2, 3, 4]);

    public void Dispose() => File.Delete(modelFile);

    [Fact]
    public void OnlyARunnerThatNamesTheCapability_MayClaimAHoldsJob()
    {
        Assert.Equal("splat,detect-holds", RunnerCapabilities.Store(["splat", "detect-holds"]));
        Assert.False(RunnerCapabilities.Allows(null, GpuJobKind.DetectHolds));
        Assert.False(RunnerCapabilities.Allows("splat,textures", GpuJobKind.DetectHolds));
        Assert.True(RunnerCapabilities.Allows("splat,detect-holds", GpuJobKind.DetectHolds));
        Assert.Equal("detect-holds", RunnerCapabilities.KindName(GpuJobKind.DetectHolds));
    }

    [Fact]
    public async Task WithoutARunnerThatFindsHolds_NothingIsQueued_AndTheAppDetects()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, Options());
        var (old, _) = await f.AddRunnerAsync("old", walls: h.WallId);
        await f.Queue.HelloAsync(old, TexturesJobSupport.Hello(20000, RunnerCapabilities.Splat, RunnerCapabilities.Textures), CancellationToken.None);
        var context = await SeedAsync(f);

        Assert.Null(await Detection(f).DetectAsync(context, CancellationToken.None));
        await using var db = h.CreateContext();
        Assert.Empty(await db.GpuJobs.ToListAsync());
    }

    [Fact]
    public async Task TheFollowUpStep_FallsBackToTheAppsSearch_WhenNoRunnerTakesIt()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, Options());
        await HoldsRunnerAsync(f);
        var context = await SeedAsync(f);
        var proposals = new FakeHoldProposals { Proposals = 4 };
        var step = new FindHoldProposalsFollowUpStep(proposals, h.DbContextFactory, Detection(f));

        var run = step.RunAsync(context, CancellationToken.None);
        await QueuedJobAsync(h);
        f.Clock.Advance(TimeSpan.FromMinutes(11)); // the runner never claims

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CaptureFollowUpOutcome.Done, result.Outcome);
        Assert.Equal(1, proposals.Calls); // the app's own search ran
    }

    [Fact]
    public async Task AJobNobodyClaims_IsCancelledAfterTheClaimWait_AndAnOlderRunnerNeverGetsIt()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, Options());
        var holds = await HoldsRunnerAsync(f);
        var (old, _) = await f.AddRunnerAsync("old", walls: h.WallId);
        var context = await SeedAsync(f);

        var detect = Detection(f).DetectAsync(context, CancellationToken.None);
        var job = await QueuedJobAsync(h);
        Assert.NotEqual(holds.Id, old.Id);
        Assert.Null(await f.Queue.TryClaimAsync(old, null, CancellationToken.None)); // an older runner is never offered it
        f.Clock.Advance(TimeSpan.FromMinutes(11)); // and the one that could take it never claims

        Assert.Null(await detect.WaitAsync(TimeSpan.FromSeconds(10)));
        var ended = await TexturesJobSupport.JobAsync(h, job.Id);
        Assert.Equal(GpuJobStatus.Cancelled, ended.Status);
        Assert.False(File.Exists(f.Files.ResolvePhysicalPath(ended.BundlePath)));
    }

    [Fact]
    public async Task ADeliveredResult_IsTheHoldsPerPhoto_AndTheJobIsClosed()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, Options());
        var runner = await HoldsRunnerAsync(f);
        var context = await SeedAsync(f);

        var detect = Detection(f).DetectAsync(context, CancellationToken.None);
        var job = await QueuedJobAsync(h);
        using (var zip = ZipFile.OpenRead(f.Files.ResolvePhysicalPath(job.BundlePath)!))
        {
            Assert.Equal(
                ["holds-job.json", "model.onnx", "photos/p00.jpg", "photos/p01.jpg"],
                zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }

        Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(runner, null, CancellationToken.None))?.Id);
        var result = "{\"version\":1,\"photos\":{\"p00\":{\"width\":64,\"height\":48,\"boxes\":[[10,10,20,20,0.9,\"hold\"],[0,0,60,40,0.8,\"volume\"]]}}}";
        Assert.Equal(
            RunnerJobOutcome.Ok,
            await f.Queue.AcceptResultAsync(runner, job.Id, new MemoryStream(Encoding.UTF8.GetBytes(result)), null, null, CancellationToken.None));

        var found = await detect.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(found);
        Assert.StartsWith("yolo-runner:", found!.Detector, StringComparison.Ordinal);
        var p00 = Assert.Single(found.Photos).Value;
        var hold = Assert.Single(p00.Detections); // the volume is left out, as in the app
        Assert.Equal(15.0, hold.Px, 0.01); // normalised and rounded to 4 places first, as in the app
        Assert.Equal(15.0, hold.Py, 0.01);
        Assert.Equal(0.9, hold.Confidence);
        var closed = await TexturesJobSupport.JobAsync(h, job.Id);
        Assert.NotNull(closed.InstalledAt);
        Assert.Null(closed.ResultPath);
    }

    [Fact]
    public async Task AFailedJob_LetsTheAppDetect()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, Options());
        var runner = await HoldsRunnerAsync(f);
        var context = await SeedAsync(f);

        var detect = Detection(f).DetectAsync(context, CancellationToken.None);
        var job = await QueuedJobAsync(h);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);
        await f.Queue.FailAsync(runner, job.Id, new RunnerFailure("bad bundle", Retryable: false), CancellationToken.None);

        Assert.Null(await detect.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData("{\"version\":2,\"photos\":{}}")]
    [InlineData("{\"version\":1,\"photos\":{\"p00\":{\"width\":0,\"height\":48,\"boxes\":[]}}}")]
    [InlineData("{\"version\":1,\"photos\":{\"p00\":{\"width\":64,\"height\":48,\"boxes\":[[1,2,3]]}}}")]
    [InlineData("not json")]
    public void AMalformedResult_IsRefused(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"holds-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            Assert.Throws<InvalidDataException>(() => RunnerHoldsResult.Validate(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Boxes_BecomeHoldsLikeTheAppsDetectorMakesThem()
    {
        // TiledDetectionContractTests' boxes: centre normalised and rounded to 4 places, radius = long side over twice the image's.
        var holds = RunnerHoldsResult.ToDetections(
            "p00", 4000, 3000, [new RunnerHoldBox(100, 200, 160, 240, 0.9, "hold"), new RunnerHoldBox(1000, 1000, 1300, 1200, 0.8, "volume")]);
        var hold = Assert.Single(holds);
        Assert.Equal(0.0325 * 4000, hold.Px, 6);
        Assert.Equal(0.0733 * 3000, hold.Py, 6);
        Assert.Equal(0.0075 * 4000, hold.RadiusPx, 6);
    }

    private static GpuRunnerOptions Options() =>
        new() { ClaimWait = TimeSpan.Zero, HoldsPollInterval = TimeSpan.FromMilliseconds(10) };

    private RunnerHoldDetection Detection(RunnerFixture f)
    {
        var settings = new BlocwerkSettings();
        settings.HoldDetection.ModelPath = modelFile;
        return new RunnerHoldDetection(f.Queue, f.Files, f.Harness.RootContextFactory, settings, NullLogger<RunnerHoldDetection>.Instance, f.Clock);
    }

    private static async Task<GpuRunner> HoldsRunnerAsync(RunnerFixture f)
    {
        var (runner, _) = await f.AddRunnerAsync("pc", walls: f.Harness.WallId);
        await f.Queue.HelloAsync(runner, TexturesJobSupport.Hello(20000, RunnerCapabilities.Splat, RunnerCapabilities.DetectHolds), CancellationToken.None);
        return runner;
    }

    private static async Task<GpuJob> QueuedJobAsync(WallTestHarness h)
    {
        for (var i = 0; i < 400; i++)
        {
            await using var db = h.CreateContext();
            if (await db.GpuJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Kind == GpuJobKind.DetectHolds) is { } job)
            {
                return job;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("no hold detection job was queued");
    }

    /// <summary>A finished capture whose active model solved two cameras, with both photos in the store.</summary>
    private static async Task<CaptureFollowUpContext> SeedAsync(RunnerFixture f)
    {
        var h = f.Harness;
        await using var db = h.CreateContext();
        var model = new WallGeometryModel { WallId = h.WallId, Json = Cameras, Source = "test", IsActive = true };
        var capture = new WallCapture
        {
            WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallCaptureStatus.Succeeded, Stage = "Done", GeometryModelId = model.Id,
        };
        db.WallGeometryModels.Add(model);
        db.WallCaptures.Add(capture);
        for (var i = 0; i < 2; i++)
        {
            var stored = await f.Files.SaveAsync(CaptureScenario.TinyJpeg(i), ".jpg", CancellationToken.None);
            db.WallCapturePhotos.Add(new WallCapturePhoto { CaptureId = capture.Id, Index = i, StoredPath = stored, ContentHash = $"h{i}" });
        }

        await db.SaveChangesAsync();
        return new CaptureFollowUpContext(capture.Id, h.WallId, model.Id, h.Owner.Id, null);
    }
}
