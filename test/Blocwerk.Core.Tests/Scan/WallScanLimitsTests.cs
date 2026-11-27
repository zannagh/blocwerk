// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Scan;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Tests.Scan;

/// <summary>
/// The upload's limits: open scans per user and per wall, free space checked on every PUT, a scan continued only by
/// the user who began it (or an administrator of the installation), and the files of one scan arriving in parallel.
/// </summary>
public class WallScanLimitsTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task AThirdOpenScanOfOneUser_IsRefused_ButResumingOneIsNot()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (first, _) = ScanPackageBuilder.Build(h.WallId);
        await s.Service.BeginAsync(h.WallId, first, None);
        await s.Service.BeginAsync(h.WallId, ScanPackageBuilder.Build(h.WallId).Manifest, None);

        var third = await Assert.ThrowsAsync<ScanUploadException>(
            () => s.Service.BeginAsync(h.WallId, ScanPackageBuilder.Build(h.WallId).Manifest, None));

        Assert.Equal(ScanUploadException.Conflict, third.StatusCode);
        Assert.Contains("2 scan uploads open", third.Message, StringComparison.Ordinal);
        Assert.Equal(ScanUploadState.Uploading, (await s.Service.BeginAsync(h.WallId, first, None)).Status);
    }

    [Fact]
    public async Task AFifthOpenScanOfOneWall_IsRefused()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        foreach (var who in new[] { "mod-a", "mod-b" })
        {
            h.ActingUser = await h.AddMemberAsync(who, WallRole.Moderator);
            await s.Service.BeginAsync(h.WallId, ScanPackageBuilder.Build(h.WallId).Manifest, None);
            await s.Service.BeginAsync(h.WallId, ScanPackageBuilder.Build(h.WallId).Manifest, None);
        }

        h.ActingUser = await h.AddMemberAsync("mod-c", WallRole.Moderator);
        var fifth = await Assert.ThrowsAsync<ScanUploadException>(
            () => s.Service.BeginAsync(h.WallId, ScanPackageBuilder.Build(h.WallId).Manifest, None));

        Assert.Equal(ScanUploadException.Conflict, fifth.StatusCode);
        Assert.Contains("This wall has 4", fifth.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APut_IsRefusedWhenTheDiskIsFull()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (manifest, files) = ScanPackageBuilder.Build(h.WallId);
        var state = await s.Service.BeginAsync(h.WallId, manifest, None);
        s.Service.FreeSpace = () => 1024;

        var full = await Assert.ThrowsAsync<ScanUploadException>(() => s.PutAsync(state.ScanId, "mesh/arkit.ply", files["mesh/arkit.ply"]));

        Assert.Equal(ScanUploadException.Conflict, full.StatusCode);
        Assert.False(File.Exists(s.Storage.FilePath(state.ScanId, "mesh/arkit.ply")));
    }

    [Fact]
    public async Task AnotherModerator_MayNotContinueTheScan_AnInstallationAdminMay()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (manifest, files) = ScanPackageBuilder.Build(h.WallId);
        var state = await s.Service.BeginAsync(h.WallId, manifest, None);
        h.ActingUser = await h.AddMemberAsync("other-mod", WallRole.Moderator);

        var begin = await Assert.ThrowsAsync<ScanUploadException>(() => s.Service.BeginAsync(h.WallId, manifest, None));
        var put = await Assert.ThrowsAsync<ScanUploadException>(() => s.PutAsync(state.ScanId, "mesh/arkit.ply", files["mesh/arkit.ply"]));
        var commit = await Assert.ThrowsAsync<ScanUploadException>(() => s.Service.CommitAsync(h.WallId, state.ScanId, None));

        Assert.All(new[] { begin, put, commit }, e => Assert.Equal(ScanUploadException.Forbidden, e.StatusCode));
        await using (var db = h.CreateContext())
        {
            var admin = new User { Identifier = "site-admin", DisplayName = "Site admin", Role = IdentityRole.Admin };
            db.Users.Add(admin);
            await db.SaveChangesAsync();
            h.ActingUser = admin;
        }

        await s.PutMissingAsync(await s.Service.BeginAsync(h.WallId, manifest, None), files);
        Assert.Equal(ScanUploadState.Received, (await s.Service.CommitAsync(h.WallId, state.ScanId, None)).Status);
    }

    [Fact]
    public async Task TheFilesOfOneScan_StreamInParallel()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        var (manifest, files) = ScanPackageBuilder.Build(h.WallId);
        var state = await s.Service.BeginAsync(h.WallId, manifest, None);
        var slow = state.Missing[0];
        var others = state.Missing.Skip(1).ToList();
        var gate = new TaskCompletionSource();
        var held = new GatedStream(files[slow], gate.Task);

        // The first file stalls until every other file of the scan is in: with one lock per scan this would never finish.
        var stalled = s.Service.PutFileAsync(h.WallId, state.ScanId, slow, null, files[slow].Length, held, None);
        var rest = await Task.WhenAll(others.Select(n => Task.Run(() => s.PutAsync(state.ScanId, n, files[n]))))
            .WaitAsync(TimeSpan.FromSeconds(30));
        gate.SetResult();
        var first = await stalled.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(others, rest.Select(r => r.Name));
        Assert.Equal((slow, ScanFileState.Received), (first.Name, first.Status));
        Assert.Equal(ScanUploadState.Received, (await s.Service.CommitAsync(h.WallId, state.ScanId, None)).Status);
    }
}
