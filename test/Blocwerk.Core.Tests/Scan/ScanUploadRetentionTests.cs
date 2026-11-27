// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Retention;
using Blocwerk.Core.Capture.Scan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.Scan;

/// <summary>An abandoned scan upload goes with its draft; a received scan, and its reserved point cloud, stay.</summary>
public class ScanUploadRetentionTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task AnAbandonedUpload_IsRemovedWithItsDraft_AReceivedScanStays()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (open, openFiles) = ScanPackageBuilder.Build(h.WallId);
        await s.PutMissingAsync(await s.Service.BeginAsync(h.WallId, open, None), openFiles, limit: 2);
        var (done, doneFiles) = ScanPackageBuilder.Build(h.WallId);
        await s.PutMissingAsync(await s.Service.BeginAsync(h.WallId, done, None), doneFiles);
        await s.Service.CommitAsync(h.WallId, done.ScanId!.Value, None);
        var pointCloud = s.Storage.FilePath(done.ScanId.Value, ScanStorage.PointCloudFile);
        Directory.CreateDirectory(Path.GetDirectoryName(pointCloud)!);
        await File.WriteAllBytesAsync(pointCloud, [1, 2, 3]);

        var options = new WallCapturePipelineOptions { RetentionDryRun = false };
        var later = DateTimeOffset.UtcNow + options.ImportStagingLifetime + TimeSpan.FromHours(1);
        var outcome = await ScanUploadRetention.RunAsync(h.RootContextFactory, s.Files, options, later, NullLogger.Instance, None);

        Assert.Equal(1, outcome.Count);
        Assert.False(Directory.Exists(s.Storage.Folder(open.ScanId!.Value)));
        Assert.True(File.Exists(pointCloud));
        await using var db = h.CreateContext();
        Assert.False(await db.WallCaptures.AnyAsync(c => c.Id == open.ScanId));
        Assert.False(await db.WallScans.AnyAsync(x => x.CaptureId == open.ScanId));
        Assert.True(await db.WallScans.AnyAsync(x => x.CaptureId == done.ScanId));
    }

    [Fact]
    public async Task AnUploadStillInUse_IsKept()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (open, _) = ScanPackageBuilder.Build(h.WallId);
        await s.Service.BeginAsync(h.WallId, open, None);

        var outcome = await ScanUploadRetention.RunAsync(
            h.RootContextFactory, s.Files, new WallCapturePipelineOptions { RetentionDryRun = false }, DateTimeOffset.UtcNow, NullLogger.Instance, None);

        Assert.Equal(0, outcome.Count);
        Assert.True(Directory.Exists(s.Storage.Folder(open.ScanId!.Value)));
    }

    [Fact]
    public async Task InADryRun_TheSweepStillRemovesAbandonedUploads_AndCountsTheirBytesAsFreed()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (open, openFiles) = ScanPackageBuilder.Build(h.WallId);
        await s.PutMissingAsync(await s.Service.BeginAsync(h.WallId, open, None), openFiles, limit: 2);
        var (done, doneFiles) = ScanPackageBuilder.Build(h.WallId);
        await s.PutMissingAsync(await s.Service.BeginAsync(h.WallId, done, None), doneFiles);
        await s.Service.CommitAsync(h.WallId, done.ScanId!.Value, None);
        Backdate(s.Storage.Folder(open.ScanId!.Value));
        Backdate(s.Storage.Folder(done.ScanId.Value));

        var options = new WallCapturePipelineOptions { RetentionDryRun = true };
        var result = await new WallCaptureSweeper(h.RootContextFactory, s.Files, options, NullLogger<WallCaptureSweeper>.Instance)
            .SweepAsync(None);

        Assert.Equal(1, result.AbandonedScans.Count);
        Assert.True(result.AbandonedScans.Bytes > 0);
        Assert.Equal(result.AbandonedScans.Bytes, result.FreedBytes);
        Assert.False(Directory.Exists(s.Storage.Folder(open.ScanId.Value)));
        Assert.True(File.Exists(s.Storage.FilePath(done.ScanId.Value, "mesh/arkit.ply")));
        await using var db = h.CreateContext();
        Assert.False(await db.WallCaptures.AnyAsync(c => c.Id == open.ScanId));
        Assert.True(await db.WallScans.AnyAsync(x => x.CaptureId == done.ScanId));
    }

    [Fact]
    public async Task AResumedUpload_IsKeptRightAfterTheResume()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (open, openFiles) = ScanPackageBuilder.Build(h.WallId);
        await s.PutMissingAsync(await s.Service.BeginAsync(h.WallId, open, None), openFiles, limit: 2);
        Backdate(s.Storage.Folder(open.ScanId!.Value));

        await s.Service.BeginAsync(h.WallId, open, None);
        var options = new WallCapturePipelineOptions { RetentionDryRun = false };
        var outcome = await ScanUploadRetention.RunAsync(
            h.RootContextFactory, s.Files, options, DateTimeOffset.UtcNow + options.ImportStagingLifetime - TimeSpan.FromHours(1), NullLogger.Instance, None);

        Assert.Equal(0, outcome.Count);
        Assert.True(File.Exists(s.Storage.FilePath(open.ScanId.Value, "mesh/arkit.ply")));
    }

    private static void Backdate(string folder)
    {
        var old = DateTime.UtcNow.AddDays(-30);
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Reverse())
        {
            entry.LastWriteTimeUtc = old;
        }

        Directory.SetLastWriteTimeUtc(folder, old);
    }
}
