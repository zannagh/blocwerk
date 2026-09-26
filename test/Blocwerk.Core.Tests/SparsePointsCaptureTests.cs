// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Sparse;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Every splat-prepare a capture runs leaves its sparse points on the capture (the markerless reconstruction, and the CPU
/// half of a runner-trained photo-real view of a marker capture), so volumes and protrusion can be measured without a
/// photo-real view; they follow the photos' retention.
/// </summary>
public class SparsePointsCaptureTests
{
    [Fact]
    public async Task AMarkerlessCapture_KeepsItsReconstructionsSparsePoints()
    {
        using var h = new WallTestHarness();
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector { Ids = [] });
        s.Client.GeometryJson = MarkerlessFixture.FeatureDoc(anchored: false);
        var captureId = await MarkerlessFixture.StartAsync(s);

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        var cloud = SparseCloudFile.Read((await s.Files.ReadAsync(capture.SparsePointsStoredPath!, CancellationToken.None))!);
        Assert.Equal(6, cloud.PhotoCentres.Count);
    }

    [Fact]
    public async Task AMarkerCaptureTrainedOnARunner_KeepsThePrepareStepsSparsePoints()
    {
        using var h = new WallTestHarness();
        var runners = new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, Mode = GpuRunnerMode.Always };
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector(), runners: runners, clock: new MutableTestClock(DateTimeOffset.UtcNow));
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureGeometryMode.Markers, capture.GeometryMode);
        Assert.StartsWith(WallCaptureProcessor.PrepareMark, capture.SplatJobId);
        Assert.NotNull(capture.SparsePointsStoredPath);
        Assert.NotNull(await s.Files.ReadAsync(capture.SparsePointsStoredPath, CancellationToken.None));
    }

    [Fact]
    public async Task WithoutAPrepareStep_ThereAreNoSparsePoints()
    {
        using var h = new WallTestHarness();
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector());
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        Assert.Null((await db.WallCaptures.SingleAsync()).SparsePointsStoredPath);
    }

    [Fact]
    public async Task ExpiredCapture_LosesItsSparsePoints_TheActiveCaptureKeepsThem_AndStrayOnesAreOrphans()
    {
        using var h = new WallTestHarness();
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector { Ids = [] });
        s.Client.GeometryJson = MarkerlessFixture.FeatureDoc(anchored: false);
        var active = await MarkerlessFixture.StartAsync(s);
        await s.Processor.ProcessAsync(active, CancellationToken.None);
        var stray = await s.Files.SaveAsync([1, 2, 3], SparseCloudFile.Extension, CancellationToken.None);
        Guid old;
        string oldFile;
        await using (var db = h.CreateContext())
        {
            oldFile = await s.Files.SaveAsync([4, 5, 6], SparseCloudFile.Extension, CancellationToken.None);
            var capture = new WallCapture
            {
                WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallCaptureStatus.Succeeded,
                CompletedAt = DateTimeOffset.UtcNow.AddDays(-31), SparsePointsStoredPath = oldFile,
            };
            db.WallCaptures.Add(capture);
            await db.SaveChangesAsync();
            old = capture.Id;
        }

        foreach (var file in s.Files.ListFiles())
        {
            File.SetLastWriteTimeUtc(s.Files.ResolvePhysicalPath(file.Name)!, DateTime.UtcNow.AddHours(-3));
        }

        await new WallCaptureSweeper(h.RootContextFactory, s.Files, s.Options, NullLogger<WallCaptureSweeper>.Instance).SweepAsync(CancellationToken.None);

        await using var check = h.CreateContext();
        Assert.Null((await check.WallCaptures.SingleAsync(c => c.Id == old)).SparsePointsStoredPath);
        var kept = (await check.WallCaptures.SingleAsync(c => c.Id == active)).SparsePointsStoredPath!;
        var left = s.Files.ListFiles().Select(f => f.Name).ToHashSet();
        Assert.Contains(kept, left);
        Assert.DoesNotContain(oldFile, left);
        Assert.DoesNotContain(stray, left);
    }
}
