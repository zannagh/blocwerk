// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>"Register panel photos on a 3D runner": only with a runner that can, one open job per wall, poses kept.</summary>
public sealed class PanelRegistrationServiceTests : IDisposable
{
    private readonly string storeDir = Path.Combine(Path.GetTempPath(), "blocwerk-panel-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(storeDir))
        {
            Directory.Delete(storeDir, recursive: true);
        }
    }

    [Fact]
    public async Task Register_IsRefused_WithoutARunnerThatRegistersPanels()
    {
        using var h = new WallTestHarness();
        var (_, service) = await SeedAsync(h, "splat,detect-holds");

        Assert.False((await service.GetSurfaceRegistrationAsync(h.WallId)).RunnerReady);
        await Assert.ThrowsAsync<UserFacingException>(() => service.RegisterPanelsAsync(h.WallId));
    }

    [Fact]
    public async Task Register_ReturnsTheOpenJob_InsteadOfQueueingAnother()
    {
        using var h = new WallTestHarness();
        var (s, service) = await SeedAsync(h, "splat,register-panels");
        await AddJobAsync(h, s.ModelId, GpuJobStatus.Queued);

        var registration = await service.RegisterPanelsAsync(h.WallId);

        Assert.Equal("queued", registration.State);
        Assert.True((await service.GetSurfaceRegistrationAsync(h.WallId)).RunnerReady);
        await using var db = h.CreateContext();
        Assert.Equal(1, await db.GpuJobs.CountAsync(j => j.Kind == GpuJobKind.RegisterPanels));
    }

    [Fact]
    public async Task Retention_KeepsThePanelPoses()
    {
        using var h = new WallTestHarness();
        var (s, _) = await SeedAsync(h, "splat,register-panels");
        var job = await AddJobAsync(h, s.ModelId, GpuJobStatus.Succeeded, installedDaysAgo: 90);
        var files = Files();
        var options = new WallCapturePipelineOptions { RetentionDryRun = false };

        await GpuJobQueue.DropAgedResultsAsync(h.RootContextFactory, files, options, DateTimeOffset.UtcNow, NullLogger.Instance, CancellationToken.None);

        await using var db = h.CreateContext();
        Assert.Equal(job.ResultPath, (await db.GpuJobs.SingleAsync(j => j.Id == job.Id)).ResultPath);
    }

    private FileSystemCaptureFileStore Files() => new(new Configuration.BlocwerkSettings { WallImage = { StoragePath = storeDir } });

    private async Task<(HoldPlacementScenario S, HoldTexturePlacementService Service)> SeedAsync(WallTestHarness h, string capabilities)
    {
        var s = await HoldPlacementScenario.CreateAsync(h);
        await using var db = h.CreateContext();
        var runner = new GpuRunner
        {
            Name = "pc", OwnerUserId = h.Owner.Id, KeyHash = "k" + Guid.NewGuid().ToString("N"), KeyPrefix = "bwr_test",
            LastSeenAt = DateTimeOffset.UtcNow, Capabilities = capabilities,
        };
        db.GpuRunners.Add(runner);
        db.GpuRunnerWalls.Add(new GpuRunnerWall { RunnerId = runner.Id, WallId = h.WallId });
        await db.SaveChangesAsync();
        var queue = new GpuJobQueue(h.RootContextFactory, Files(), new WallCaptureQueue(), new GpuRunnerOptions(), new GpuJobSignal(), NullLogger<GpuJobQueue>.Instance);
        return (s, new HoldTexturePlacementService(
            h.DbContextFactory, h.CurrentUser, NullLogger<HoldTexturePlacementService>.Instance, s.Matcher, s.Files, s.Queue, gpuJobs: queue));
    }

    private async Task<GpuJob> AddJobAsync(WallTestHarness h, Guid modelId, GpuJobStatus status, int? installedDaysAgo = null)
    {
        await using var db = h.CreateContext();
        var capture = new WallCapture { WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallCaptureStatus.Succeeded, Stage = "Done", GeometryModelId = modelId };
        db.WallCaptures.Add(capture);
        var job = new GpuJob
        {
            WallId = h.WallId, CaptureId = capture.Id, GeometryModelId = modelId, Kind = GpuJobKind.RegisterPanels, Status = status,
            BundlePath = "b.zip", BundleSha256 = "x", PreparedPath = "b.zip",
            ResultPath = installedDaysAgo is null ? null : await Files().SaveAsync("{}"u8.ToArray(), ".json", CancellationToken.None),
            InstalledAt = installedDaysAgo is { } days ? DateTimeOffset.UtcNow.AddDays(-days) : null,
        };
        db.GpuJobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }
}
