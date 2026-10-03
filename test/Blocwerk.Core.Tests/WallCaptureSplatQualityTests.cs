// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The photo-real view's quality profile: chosen at start (default high), sent to the worker as
/// <c>options.quality</c>, and changed later by "retrain at higher quality", which resubmits the
/// stored photos and keeps the current splat until the new one is stored.
/// </summary>
public class WallCaptureSplatQualityTests
{
    [Theory]
    [InlineData(null, "{\"spz\":true}")]
    [InlineData(SplatQuality.Draft, "{\"spz\":true,\"quality\":\"draft\"}")]
    [InlineData(SplatQuality.High, "{\"spz\":true,\"quality\":\"high\"}")]
    [InlineData(SplatQuality.Max, "{\"spz\":true,\"quality\":\"max\",\"maxSteps\":900}")]
    public void BuildOptions_CarriesTheQuality(SplatQuality? quality, string expected)
    {
        var maxSteps = quality == SplatQuality.Max ? 900 : (int?)null;
        Assert.Equal(expected, CaptureSplatDocuments.BuildOptions(maxSteps, quality));
    }

    [Theory]
    [InlineData(null, SplatQuality.High)]
    [InlineData(SplatQuality.Draft, SplatQuality.High)]
    [InlineData(SplatQuality.High, SplatQuality.Max)]
    [InlineData(SplatQuality.Max, null)]
    public void NextQuality_StepsUpOnce(SplatQuality? current, SplatQuality? expected) =>
        Assert.Equal(expected, CaptureSplatDocuments.NextQuality(current));

    [Fact]
    public void TexturePart_DropsTheSplatHalfOfTheError()
    {
        Assert.Null(WallCaptureService.TexturePart(null));
        Assert.Null(WallCaptureService.TexturePart("The 3D model is active, but its photo-real view could not be made: x"));
        Assert.Equal("Textures failed.", WallCaptureService.TexturePart(
            "Textures failed. The 3D model is active, but its photo-real view could not be made: x"));
    }

    [Fact]
    public async Task Start_DefaultsToHigh_AndTheWorkerGetsIt()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        Assert.Equal(SplatQuality.High, (await db.WallCaptures.SingleAsync()).SplatQuality);
        Assert.Equal("high", QualitySent(s, 0));
        Assert.Equal(SplatQuality.High, (await s.Service.GetCaptureAsync(captureId))!.SplatQuality);
    }

    [Fact]
    public async Task Retrain_ResubmitsAtTheNewQuality_AndReplacesTheSplatOnlyOnSuccess()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        var captureId = await s.StartCaptureAsync(photos: 3);
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        WallGeometrySplat first;
        await using (var db = h.CreateContext())
        {
            first = await db.WallGeometrySplats.AsNoTracking().SingleAsync();
        }

        Assert.Empty(await s.Service.RetrainPhotoRealAsync(captureId, SplatQuality.Max));

        await using (var db = h.CreateContext())
        {
            var queued = await db.WallCaptures.SingleAsync();
            Assert.Equal(WallCaptureStatus.Splatting, queued.Status);
            Assert.Equal(SplatQuality.Max, queued.SplatQuality);
            Assert.Null(queued.SplatJobId);
            Assert.Equal(first.Id, (await db.WallGeometrySplats.SingleAsync()).Id); // still live while it trains
        }

        s.SplatClient.Spz = FakeComputeJobClient.Gzip(new byte[8192]);
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using (var db = h.CreateContext())
        {
            var capture = await db.WallCaptures.SingleAsync();
            Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
            var second = await db.WallGeometrySplats.SingleAsync();
            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(s.SplatClient.Spz.LongLength, second.SizeBytes);
            Assert.Equal(capture.GeometryModelId, second.GeometryModelId);
        }

        Assert.Equal(2, s.SplatClient.MultipartSubmissions.Count);
        Assert.Equal("max", QualitySent(s, 1));
        Assert.Equal(3, s.SplatClient.MultipartSubmissions[1].Parts.Count(p => p.Name == "photos"));
        var oldPath = s.Files.ResolvePhysicalPath(first.StoredPath);
        Assert.True(oldPath is null || !File.Exists(oldPath)); // the replaced file is gone
    }

    [Fact]
    public async Task FailedRetrain_KeepsTheOldSplat()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        var captureId = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        s.SplatClient.Terminal["splat"] = new ComputeJobStatus { Status = ComputeJobStates.Failed, Error = "train: out of memory" };
        Assert.Empty(await s.Service.RetrainPhotoRealAsync(captureId, SplatQuality.High));
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.SucceededWithoutSplat, capture.Status);
        Assert.Contains("out of memory", capture.Error);
        var splat = await db.WallGeometrySplats.SingleAsync();
        Assert.True(File.Exists(s.Files.ResolvePhysicalPath(splat.StoredPath)));
    }

    [Fact]
    public async Task Retrain_IsRefused_WhileRunning_WithoutWorker_OrWithoutPhotos()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        var captureId = await s.StartCaptureAsync();

        var running = await s.Service.RetrainPhotoRealAsync(captureId, SplatQuality.Max);
        Assert.Contains("Only a finished capture can be retrained.", running);

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        await using (var db = h.CreateContext())
        {
            db.WallCapturePhotos.RemoveRange(db.WallCapturePhotos);
            await db.SaveChangesAsync();
        }

        var expired = await s.Service.RetrainPhotoRealAsync(captureId, SplatQuality.Max);
        Assert.Contains(expired, e => e.Contains("photos were already deleted", StringComparison.Ordinal));

        s.SplatClient.IsConfigured = false;
        var off = await s.Service.RetrainPhotoRealAsync(captureId, SplatQuality.Max);
        Assert.Contains(off, e => e.Contains("No photo-real", StringComparison.Ordinal));

        await using var check = h.CreateContext();
        Assert.Equal(WallCaptureStatus.Succeeded, (await check.WallCaptures.SingleAsync()).Status);
    }

    private static string? QualitySent(CaptureScenario s, int submission) =>
        JsonNode.Parse(s.SplatClient.MultipartSubmissions[submission].Parts.Single(p => p.Name == "options").Content)!["quality"]
            ?.GetValue<string>();
}
