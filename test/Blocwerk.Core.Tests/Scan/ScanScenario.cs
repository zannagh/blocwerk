// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Scan;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.Scan;

/// <summary>The scan upload over a <see cref="WallTestHarness"/> and a temp-dir capture store, on a wall set to "iPhone scan".</summary>
internal sealed class ScanScenario : IDisposable
{
    private readonly string storeDir = Path.Combine(Path.GetTempPath(), "blocwerk-scan-tests", Guid.NewGuid().ToString("N"));

    public ScanScenario(WallTestHarness harness, IKioskContext? kiosk = null, ScanUploadOptions? options = null)
    {
        Harness = harness;
        var settings = new BlocwerkSettings();
        settings.WallImage.StoragePath = storeDir;
        Files = new FileSystemCaptureFileStore(settings);
        Storage = new ScanStorage(Files);
        Service = new WallScanService(harness.DbContextFactory, harness.CurrentUser, Files, NullLogger<WallScanService>.Instance, kiosk, options);
        Modes = new WallUpdateModeService(harness.DbContextFactory, harness.CurrentUser, NullLogger<WallUpdateModeService>.Instance, kiosk);
    }

    public WallTestHarness Harness { get; }

    public ICaptureFileStore Files { get; }

    public ScanStorage Storage { get; }

    public WallScanService Service { get; }

    public WallUpdateModeService Modes { get; }

    /// <summary>Seeds the harness wall and switches it to scans.</summary>
    public async Task SeedScanWallAsync()
    {
        await Harness.SeedWallAsync();
        await using var db = Harness.CreateContext();
        var wall = await db.Walls.IgnoreQueryFilters().FirstAsync(w => w.Id == Harness.WallId);
        wall.UpdateMode = WallUpdateMode.Scan;
        await db.SaveChangesAsync();
    }

    /// <summary>Sends every file the state names as missing (or only the first <paramref name="limit"/>); answers the new state.</summary>
    public async Task<ScanUploadState> PutMissingAsync(ScanUploadState state, Dictionary<string, byte[]> files, int limit = int.MaxValue)
    {
        foreach (var name in state.Missing.Take(limit))
        {
            await PutAsync(state.ScanId, name, files[name]);
        }

        return await Service.GetAsync(Harness.WallId, state.ScanId, CancellationToken.None);
    }

    public Task<ScanFileState> PutAsync(Guid scanId, string name, byte[] bytes, string? sha = null) =>
        Service.PutFileAsync(Harness.WallId, scanId, name, sha, bytes.Length, new MemoryStream(bytes), CancellationToken.None);

    /// <summary>Begins, sends and commits a whole package; answers its manifest.</summary>
    public async Task<ScanManifest> ReceiveAsync()
    {
        var (manifest, files) = ScanPackageBuilder.Build(Harness.WallId);
        await PutMissingAsync(await Service.BeginAsync(Harness.WallId, manifest, CancellationToken.None), files);
        await Service.CommitAsync(Harness.WallId, manifest.ScanId!.Value, CancellationToken.None);
        return manifest;
    }

    /// <summary>Adds an administrator of the installation and acts as them.</summary>
    public async Task ActAsSiteAdminAsync()
    {
        await using var db = Harness.CreateContext();
        var admin = new User { Identifier = $"site-admin-{Guid.NewGuid():N}", DisplayName = "Site admin", Role = IdentityRole.Admin };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        Harness.ActingUser = admin;
    }

    public void Dispose()
    {
        if (Directory.Exists(storeDir))
        {
            Directory.Delete(storeDir, recursive: true);
        }
    }
}
