// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Scan;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Scan;

/// <summary>Begin, put and commit of an iPhone scan: resume, hashes, sizes, and the scan's capture and row.</summary>
public class WallScanUploadTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task AWholePackage_IsReceived_AndShowsAsReceivedOnTheCapture()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (manifest, files) = ScanPackageBuilder.Build(h.WallId);

        var begun = await s.Service.BeginAsync(h.WallId, manifest, None);
        Assert.Equal(files.Count, begun.Missing.Count);
        var sent = await s.PutMissingAsync(begun, files);
        Assert.Empty(sent.Missing);
        var committed = await s.Service.CommitAsync(h.WallId, begun.ScanId, None);

        Assert.Equal(ScanUploadState.Received, committed.Status);
        Assert.Equal(WallScanService.ReceivedStage, committed.Stage);
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == manifest.ScanId);
        Assert.Equal((WallCaptureGeometryMode.Device, WallCaptureStatus.Received), (capture.GeometryMode, capture.Status));
        var scan = await db.WallScans.SingleAsync(x => x.CaptureId == manifest.ScanId);
        Assert.NotNull(scan.ReceivedAt);
        Assert.Equal(files.Values.Sum(b => (long)b.Length), scan.TotalBytes);
        Assert.Null(scan.PointCloudPath);
        Assert.Equal(files["mesh/arkit.ply"], await File.ReadAllBytesAsync(s.Storage.FilePath(capture.Id, "mesh/arkit.ply")));
        var listed = Assert.Single(await s.Service.ListAsync(h.WallId, None));
        Assert.Equal((capture.Id, WallScanService.ReceivedStage), (listed.ScanId, listed.Stage));
    }

    [Fact]
    public async Task ARerunAfterAnInterruption_SendsOnlyTheMissingFiles()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (manifest, files) = ScanPackageBuilder.Build(h.WallId);
        var first = await s.Service.BeginAsync(h.WallId, manifest, None);
        await s.PutMissingAsync(first, files, limit: 3);

        var again = await s.Service.BeginAsync(h.WallId, manifest, None);

        Assert.Equal(first.ScanId, again.ScanId);
        Assert.Equal(files.Count - 3, again.Missing.Count);
        Assert.DoesNotContain(first.Missing[0], again.Missing);
        Assert.Empty((await s.PutMissingAsync(again, files)).Missing);
        Assert.Equal(ScanUploadState.Received, (await s.Service.CommitAsync(h.WallId, first.ScanId, None)).Status);
    }

    [Fact]
    public async Task AFileWithTheWrongBytes_IsRefused_AndNothingIsKept()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (manifest, files) = ScanPackageBuilder.Build(h.WallId);
        var state = await s.Service.BeginAsync(h.WallId, manifest, None);
        var tampered = files["mesh/arkit.ply"].ToArray();
        tampered[0] ^= 0xFF;

        var bad = await Assert.ThrowsAsync<ScanUploadException>(() => s.PutAsync(state.ScanId, "mesh/arkit.ply", tampered));
        var wrongHeader = await Assert.ThrowsAsync<ScanUploadException>(
            () => s.PutAsync(state.ScanId, "mesh/arkit.ply", files["mesh/arkit.ply"], sha: new string('0', 64)));

        Assert.Equal(ScanUploadException.Unprocessable, bad.StatusCode);
        Assert.Equal(ScanUploadException.Unprocessable, wrongHeader.StatusCode);
        Assert.False(File.Exists(s.Storage.FilePath(state.ScanId, "mesh/arkit.ply")));
        Assert.Empty(Directory.EnumerateFiles(s.Storage.Folder(state.ScanId), "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AFileLongerThanTheManifestSays_IsRefusedAsTooLarge()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (manifest, files) = ScanPackageBuilder.Build(h.WallId);
        var state = await s.Service.BeginAsync(h.WallId, manifest, None);
        var longer = files["mesh/arkit.ply"].Concat(new byte[10]).ToArray();

        var announced = await Assert.ThrowsAsync<ScanUploadException>(() => s.PutAsync(state.ScanId, "mesh/arkit.ply", longer));
        var streamed = await Assert.ThrowsAsync<ScanUploadException>(() => s.Service.PutFileAsync(
            h.WallId, state.ScanId, "mesh/arkit.ply", null, null, new MemoryStream(longer), None));

        Assert.Equal(ScanUploadException.TooLarge, announced.StatusCode);
        Assert.Equal(ScanUploadException.TooLarge, streamed.StatusCode);
    }

    [Fact]
    public async Task ACommitWithFilesMissing_NamesThem()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (manifest, files) = ScanPackageBuilder.Build(h.WallId);
        var state = await s.PutMissingAsync(await s.Service.BeginAsync(h.WallId, manifest, None), files, limit: 1);

        var refused = await Assert.ThrowsAsync<ScanUploadException>(() => s.Service.CommitAsync(h.WallId, state.ScanId, None));

        Assert.Equal(ScanUploadException.Conflict, refused.StatusCode);
        Assert.Equal(files.Count - 1, refused.Problems.Count);
    }

    [Fact]
    public async Task AnInvalidManifest_IsRefusedWithItsProblems_AndOpensNothing()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (manifest, _) = ScanPackageBuilder.Build(h.WallId);

        var refused = await Assert.ThrowsAsync<ScanUploadException>(
            () => s.Service.BeginAsync(h.WallId, manifest with { SchemaVersion = "blocwerk-scan/9" }, None));

        Assert.Equal(ScanUploadException.BadRequest, refused.StatusCode);
        Assert.Single(refused.Problems);
        await using var db = h.CreateContext();
        Assert.False(await db.WallCaptures.AnyAsync());
    }

    [Fact]
    public async Task AWallNotSetToIPhoneScan_TakesNoScan()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await h.SeedWallAsync();
        var (manifest, _) = ScanPackageBuilder.Build(h.WallId);

        var refused = await Assert.ThrowsAsync<ScanUploadException>(() => s.Service.BeginAsync(h.WallId, manifest, None));

        Assert.Equal(ScanUploadException.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task AReceivedScan_KeepsItsPackage_AndAChangedOneNeedsANewId()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (manifest, files) = ScanPackageBuilder.Build(h.WallId);
        await s.PutMissingAsync(await s.Service.BeginAsync(h.WallId, manifest, None), files);
        await s.Service.CommitAsync(h.WallId, manifest.ScanId!.Value, None);

        var same = await s.Service.BeginAsync(h.WallId, manifest, None);
        var changed = await Assert.ThrowsAsync<ScanUploadException>(
            () => s.Service.BeginAsync(h.WallId, manifest with { App = new ScanApp("1.1", "2") }, None));

        Assert.Equal(ScanUploadState.Received, same.Status);
        Assert.Equal(ScanUploadException.Conflict, changed.StatusCode);
    }
}
