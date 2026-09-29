// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The export side of a capture package: a finished capture whose runner-trained view is kept on the server becomes a
/// manifest (rows with their ids and stored names, every file with size and SHA-256) whose files can be downloaded one by
/// one; a capture that could not be replayed without training, or a caller who is not an app admin, is refused.
/// </summary>
public class CapturePackageExportTests
{
    [Fact]
    public async Task AFinishedRunnerCapture_ExportsItsRowsAndFiles_WithHashes()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);

        var (manifest, bytes) = await f.ExportAsync();

        Assert.Equal(f.CaptureId, manifest.CaptureId);
        Assert.Equal(h.WallId, manifest.WallId);
        Assert.Equal(h.Owner.Id, manifest.OwnerUserId);
        Assert.Equal(2, manifest.Rows.Photos.Count);
        Assert.Equal(manifest.Rows.Model.Id, manifest.Rows.Capture.GeometryModelId);
        Assert.NotNull(manifest.Rows.GpuJob.ResultPath);
        Assert.NotNull(manifest.Rows.GpuJob.InstalledAt);
        var roles = manifest.Files.Select(x => x.Role).ToHashSet();
        Assert.Contains("photo", roles);
        Assert.Contains("prepared", roles);
        Assert.Contains("trained-result", roles);
        foreach (var file in manifest.Files)
        {
            Assert.Equal(file.Bytes, bytes[file.Name].LongLength);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes[file.Name])), file.Sha256);
        }

        await using var db = h.CreateContext();
        var views = await db.WallGeometrySplats.Select(s => s.StoredPath).ToListAsync();
        Assert.NotEmpty(views);
        Assert.DoesNotContain(manifest.Files, x => views.Contains(x.Name));
    }

    [Fact]
    public async Task AFileOutsideThePackage_IsNotServed()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        await using var db = h.CreateContext();
        var view = await db.WallGeometrySplats.Select(s => s.StoredPath).FirstAsync();

        Assert.Null(await f.Service.ExportFilePathAsync(f.CaptureId, view, CancellationToken.None));
        Assert.Null(await f.Service.ExportFilePathAsync(f.CaptureId, "../appsettings.json", CancellationToken.None));
    }

    [Fact]
    public async Task ACaptureWithoutAKeptRunnerResult_IsRefused()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        await using (var db = h.CreateContext())
        {
            await db.GpuJobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.ResultPath, (string?)null));
        }

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => f.Service.ExportAsync(f.CaptureId, CancellationToken.None));
        Assert.Contains("without training", ex.Message);
    }

    [Fact]
    public async Task ACaptureWhosePreparedFileIsGone_IsRefused()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        await using (var db = h.CreateContext())
        {
            f.Runner.Scenario.Files.Delete(await db.GpuJobs.Select(j => j.PreparedPath).FirstAsync());
        }

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => f.Service.ExportAsync(f.CaptureId, CancellationToken.None));
        Assert.Contains("prepared", ex.Message);
    }

    [Fact]
    public async Task AModelBuiltOnAnEarlierModel_IsRefused()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        await using (var db = h.CreateContext())
        {
            await db.WallGeometryModels.ExecuteUpdateAsync(s => s.SetProperty(m => m.DerivedFromModelId, Guid.NewGuid()));
        }

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => f.Service.ExportAsync(f.CaptureId, CancellationToken.None));
        Assert.Contains("earlier model", ex.Message);
    }

    [Fact]
    public async Task ACallerWhoIsNotAnAppAdmin_IsRefused()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h, admin: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ExportAsync(f.CaptureId, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ExportFilePathAsync(f.CaptureId, "x", CancellationToken.None));
    }
}
