// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture.Scan;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests.Scan;

/// <summary>
/// Who may upload a scan: the wall's owner, admins and moderators, and administrators of the installation; not a plain
/// member, not another wall's admin, not a kiosk tablet or a visitor. And a scan id belongs to its wall.
/// </summary>
public class WallScanAccessTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Theory]
    [InlineData(WallRole.Moderator)]
    [InlineData(WallRole.Admin)]
    public async Task AdminsAndModerators_MayUpload(WallRole role)
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        h.ActingUser = await h.AddMemberAsync("uploader", role);
        var (manifest, files) = ScanPackageBuilder.Build(h.WallId);

        await s.PutMissingAsync(await s.Service.BeginAsync(h.WallId, manifest, None), files);

        Assert.Equal(ScanUploadState.Received, (await s.Service.CommitAsync(h.WallId, manifest.ScanId!.Value, None)).Status);
    }

    [Fact]
    public async Task AnAdministratorOfTheInstallation_MayUpload()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        await using (var db = h.CreateContext())
        {
            var admin = new User { Identifier = "site-admin", DisplayName = "Site admin", Role = IdentityRole.Admin };
            db.Users.Add(admin);
            await db.SaveChangesAsync();
            h.ActingUser = admin;
        }

        var (manifest, _) = ScanPackageBuilder.Build(h.WallId);

        Assert.Equal(ScanUploadState.Uploading, (await s.Service.BeginAsync(h.WallId, manifest, None)).Status);
    }

    [Fact]
    public async Task APlainMember_IsRefused_EvenForAScanBegunByAnAdmin()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (manifest, files) = ScanPackageBuilder.Build(h.WallId);
        var state = await s.Service.BeginAsync(h.WallId, manifest, None);
        h.ActingUser = await h.AddMemberAsync("member", WallRole.Member);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.BeginAsync(h.WallId, manifest, None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.PutAsync(state.ScanId, "mesh/arkit.ply", files["mesh/arkit.ply"]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.CommitAsync(h.WallId, state.ScanId, None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.GetAsync(h.WallId, state.ScanId, None));
    }

    [Fact]
    public async Task AKioskTablet_IsRefused()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(h.WallId);
        using var s = new ScanScenario(h, kiosk);
        var (manifest, _) = ScanPackageBuilder.Build(h.WallId);

        await Assert.ThrowsAsync<KioskRestrictedException>(() => s.Service.BeginAsync(h.WallId, manifest, None));
    }

    [Fact]
    public async Task AVisitor_IsRefused()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var visit = Substitute.For<IKioskContext>();
        visit.IsKiosk.Returns(false);
        visit.VisitorWallId.Returns(h.WallId);
        using var s = new ScanScenario(h, visit);
        var (manifest, _) = ScanPackageBuilder.Build(h.WallId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.BeginAsync(h.WallId, manifest, None));
    }

    [Fact]
    public async Task AScanIsOnlyReachableThroughItsOwnWall()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (manifest, files) = ScanPackageBuilder.Build(h.WallId);
        var state = await s.Service.BeginAsync(h.WallId, manifest, None);
        var otherWall = new Wall { Name = "Other", OwnerId = h.Owner.Id, UpdateMode = WallUpdateMode.Scan };
        await using (var db = h.CreateContext())
        {
            db.Walls.Add(otherWall);
            await db.SaveChangesAsync();
        }

        var put = await Assert.ThrowsAsync<ScanUploadException>(() => s.Service.PutFileAsync(
            otherWall.Id, state.ScanId, "mesh/arkit.ply", null, null, new MemoryStream(files["mesh/arkit.ply"]), None));
        var reused = await Assert.ThrowsAsync<ScanUploadException>(
            () => s.Service.BeginAsync(otherWall.Id, manifest with { WallId = otherWall.Id }, None));

        Assert.Equal(ScanUploadException.NotFound, put.StatusCode);
        Assert.Equal(ScanUploadException.Conflict, reused.StatusCode);
    }

    [Fact]
    public async Task AScansDraft_IsNeverTheCapturePanelsDraft()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (manifest, _) = ScanPackageBuilder.Build(h.WallId);
        await s.Service.BeginAsync(h.WallId, manifest, None);

        await using var db = h.CreateContext();
        Assert.True(await db.WallCaptures.AnyAsync(c => c.Status == WallCaptureStatus.Draft && c.GeometryMode == WallCaptureGeometryMode.Device));
        using var capture = new CaptureScenario(h);
        Assert.Null(await capture.Service.GetDraftAsync(h.WallId));
    }
}
