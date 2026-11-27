// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Scan;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Scan;

/// <summary>
/// What received scans may keep on disk: a few waiting per wall, so many bytes per user in the window, and a site admin
/// who removes a waiting scan to make room.
/// </summary>
public class WallScanQuotaTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task AFourthWaitingScanOfAWall_IsRefusedAtBegin_UntilASiteAdminRemovesOne()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var first = await s.ReceiveAsync();
        await s.ReceiveAsync();
        await s.ReceiveAsync();

        var fourth = await Assert.ThrowsAsync<ScanUploadException>(
            () => s.Service.BeginAsync(h.WallId, ScanPackageBuilder.Build(h.WallId).Manifest, None));
        await s.ActAsSiteAdminAsync();
        await s.Service.DeleteAsync(h.WallId, first.ScanId!.Value, None);

        Assert.Equal(ScanUploadException.Conflict, fourth.StatusCode);
        Assert.Equal("This wall already has 3 scans waiting; a site admin can remove one.", fourth.Message);
        Assert.False(Directory.Exists(s.Storage.Folder(first.ScanId.Value)));
        Assert.Equal(ScanUploadState.Uploading, (await s.Service.BeginAsync(h.WallId, ScanPackageBuilder.Build(h.WallId).Manifest, None)).Status);
    }

    [Fact]
    public async Task AScanBegunBeforeTheWallFilledUp_StaysOpenAtCommit()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h, options: new ScanUploadOptions { MaxWaitingScansPerWall = 1 });
        await s.SeedScanWallAsync();
        var (late, lateFiles) = ScanPackageBuilder.Build(h.WallId);
        await s.PutMissingAsync(await s.Service.BeginAsync(h.WallId, late, None), lateFiles);
        await s.ReceiveAsync();

        var commit = await Assert.ThrowsAsync<ScanUploadException>(() => s.Service.CommitAsync(h.WallId, late.ScanId!.Value, None));

        Assert.Equal(ScanUploadException.Conflict, commit.StatusCode);
        Assert.Equal(ScanUploadState.Uploading, (await s.Service.GetAsync(h.WallId, late.ScanId.Value, None)).Status);
    }

    [Fact]
    public async Task AUserOverTheirBytesInTheWindow_IsRefused_OverAllWalls_AndOldScansDoNotCount()
    {
        using var h = new WallTestHarness();
        var package = ScanPackageBuilder.Build(h.WallId).Manifest.Files!.Sum(f => f.Bytes!.Value);
        using var s = new ScanScenario(h, options: new ScanUploadOptions { MaxBytesPerUser = (2 * package) + 1, MaxWaitingScansPerWall = 10 });
        await s.SeedScanWallAsync();
        var old = await s.ReceiveAsync();
        await s.ReceiveAsync();

        var over = await Assert.ThrowsAsync<ScanUploadException>(
            () => s.Service.BeginAsync(h.WallId, ScanPackageBuilder.Build(h.WallId).Manifest, None));
        await using (var db = h.CreateContext())
        {
            var scan = await db.WallScans.SingleAsync(x => x.CaptureId == old.ScanId);
            scan.ReceivedAt = DateTimeOffset.UtcNow.AddDays(-31);
            await db.SaveChangesAsync();
        }

        Assert.Equal(ScanUploadException.Conflict, over.StatusCode);
        Assert.Contains("A site admin can remove one of your waiting scans.", over.Message, StringComparison.Ordinal);
        Assert.Equal(ScanUploadState.Uploading, (await s.Service.BeginAsync(h.WallId, ScanPackageBuilder.Build(h.WallId).Manifest, None)).Status);
    }

    [Fact]
    public async Task OnlyASiteAdmin_RemovesAScan_AndOnlyAReceivedOne()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var received = await s.ReceiveAsync();
        var (open, _) = ScanPackageBuilder.Build(h.WallId);
        await s.Service.BeginAsync(h.WallId, open, None);

        Assert.False(await s.Service.MayDeleteAsync(None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.DeleteAsync(h.WallId, received.ScanId!.Value, None));
        await s.ActAsSiteAdminAsync();
        var draft = await Assert.ThrowsAsync<ScanUploadException>(() => s.Service.DeleteAsync(h.WallId, open.ScanId!.Value, None));

        Assert.True(await s.Service.MayDeleteAsync(None));
        Assert.Equal(ScanUploadException.Conflict, draft.StatusCode);
        Assert.True(await s.Service.DeleteAsync(h.WallId, received.ScanId!.Value, None));
        await using var db = h.CreateContext();
        Assert.False(await db.WallCaptures.AnyAsync(c => c.Id == received.ScanId));
        Assert.False(await db.WallScans.AnyAsync(x => x.CaptureId == received.ScanId));
        Assert.True(await db.WallCaptures.AnyAsync(c => c.Id == open.ScanId && c.Status == WallCaptureStatus.Draft));
    }
}
