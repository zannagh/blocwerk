// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A capture finished through a 3D runner (<see cref="RunnerCaptureFlow"/>), exported as a package; the same instance then
/// plays the target by forgetting the capture (rows and files) before the import.
/// </summary>
internal sealed class CapturePackageFlow : IDisposable
{
    private CapturePackageFlow(WallTestHarness harness, RunnerCaptureFlow runner)
    {
        Harness = harness;
        Runner = runner;
        Service = new CapturePackageService(
            harness.RootContextFactory, harness.CurrentUser, runner.Scenario.Files, runner.Scenario.Queue,
            new FakeComputeJobClientFactory(runner.Scenario.Client, runner.Scenario.SplatClient), NullLogger<CapturePackageService>.Instance,
            runner.Runners.Options, Disk);
    }

    public WallTestHarness Harness { get; }

    public RunnerCaptureFlow Runner { get; }

    public CapturePackageService Service { get; }

    public FakeDiskSpace Disk { get; } = new();

    public Guid CaptureId => Runner.CaptureId;

    /// <summary>A capture whose runner-trained view is installed; the harness owner is an app admin unless <paramref name="admin"/> is false.</summary>
    public static async Task<CapturePackageFlow> FinishedAsync(WallTestHarness h, bool admin = true)
    {
        var runner = await RunnerCaptureFlow.StartAsync(h);
        Assert.Equal(RunnerJobOutcome.Ok, await runner.ResultAsync());
        await runner.ProcessAsync();
        if (admin)
        {
            await using var db = h.CreateContext();
            await db.Users.Where(u => u.Id == h.Owner.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, IdentityRole.Admin));
        }

        return new CapturePackageFlow(h, runner);
    }

    /// <summary>The manifest as the wire carries it (serialized and read back), with every file's bytes.</summary>
    public async Task<(CapturePackageManifest Manifest, Dictionary<string, byte[]> Bytes)> ExportAsync()
    {
        var manifest = Wire(await Service.ExportAsync(CaptureId, CancellationToken.None));
        var bytes = new Dictionary<string, byte[]>();
        foreach (var file in manifest.Files)
        {
            bytes[file.Name] = await File.ReadAllBytesAsync((await Service.ExportFilePathAsync(CaptureId, file.Name, CancellationToken.None))!);
        }

        return (manifest, bytes);
    }

    /// <summary>The instance forgets the capture: its rows (the model with its textures and views) and its files.</summary>
    public async Task ForgetAsync(CapturePackageManifest manifest, bool keepModel = false)
    {
        await using (var db = Harness.CreateContext())
        {
            await db.WallCaptures.Where(c => c.Id == manifest.CaptureId).ExecuteDeleteAsync();
            if (!keepModel)
            {
                await db.WallGeometryModels.Where(m => m.Id == manifest.Rows.Model.Id).ExecuteDeleteAsync();
            }
        }

        foreach (var file in manifest.Files)
        {
            Runner.Scenario.Files.Delete(file.Name);
        }
    }

    /// <summary>Uploads every file of the package.</summary>
    public async Task UploadAllAsync(CapturePackageManifest manifest, Dictionary<string, byte[]> bytes)
    {
        foreach (var file in manifest.Files)
        {
            await Service.PutFileAsync(manifest.CaptureId, file.Name, new MemoryStream(bytes[file.Name]), CancellationToken.None);
        }
    }

    public void Dispose() => Runner.Dispose();

    private static CapturePackageManifest Wire(CapturePackageManifest manifest) =>
        JsonSerializer.Deserialize<CapturePackageManifest>(JsonSerializer.Serialize(manifest, CapturePackageFiles.Json), CapturePackageFiles.Json)!;
}
