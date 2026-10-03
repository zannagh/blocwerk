// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Retention;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The disk retention beyond photos: 3D runners' trained results once the view is long installed, abandoned capture
/// imports, unreferenced photo-real scenes, and the settings that tune (or switch off) each rule.
/// </summary>
public class CaptureRetentionRulesTests
{
    [Fact]
    public async Task TrainedResult_GoesAfterTheRetention_TheInstalledViewAndRecentOnesStay()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var old = await InstalledJobAsync(f, daysAgo: 40);
        var recent = await InstalledJobAsync(f, daysAgo: 5);
        var options = new WallCapturePipelineOptions { RetentionDryRun = false };

        var expected = new FileInfo(f.Files.ResolvePhysicalPath(old.ResultPath!)!).Length
                       + new FileInfo(f.Files.ResolvePhysicalPath(old.PreparedPath)!).Length;

        var outcome = await DropAsync(f, options);

        Assert.Equal(new RetentionOutcome(1, expected), outcome); // what was really deleted
        Assert.False(Exists(f.Files, old.ResultPath!));
        Assert.False(Exists(f.Files, old.PreparedPath));
        Assert.True(Exists(f.Files, recent.ResultPath!));
        await using (var db = h.CreateContext())
        {
            var dropped = await db.GpuJobs.SingleAsync(j => j.Id == old.Id);
            Assert.Null(dropped.ResultPath);
            Assert.NotNull(dropped.LeftoverDroppedAt); // PreparedPath (required) names a file that is gone
            Assert.Null((await db.GpuJobs.SingleAsync(j => j.Id == recent.Id)).LeftoverDroppedAt);
            Assert.NotNull((await db.GpuJobs.SingleAsync(j => j.Id == recent.Id)).ResultPath);

            // No longer offered for a re-finish: its leftover is gone.
            Assert.DoesNotContain(old.CaptureId, await GpuJobQueue.RefinishableAsync(db, f.Files, [old.CaptureId, recent.CaptureId]));
        }

        Assert.Equal(0, (await DropAsync(f, options)).Count); // idempotent
    }

    [Fact]
    public async Task TrainedResult_OfAJobBeingFinishedAgain_OrInDryRun_OrWithKeepForever_Stays()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var job = await InstalledJobAsync(f, daysAgo: 40);

        var dry = await DropAsync(f, new WallCapturePipelineOptions { RetentionDryRun = true });
        Assert.Equal(1, dry.Count);
        Assert.Equal(0, (await DropAsync(f, new WallCapturePipelineOptions { RunnerLeftoverRetention = null, RetentionDryRun = false })).Count);
        await using (var db = h.CreateContext())
        {
            await db.GpuJobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.RefinishStateJson, "{}"));
        }

        Assert.Equal(0, (await DropAsync(f, new WallCapturePipelineOptions { RetentionDryRun = false })).Count);
        Assert.True(Exists(f.Files, job.ResultPath!));
        Assert.True(Exists(f.Files, job.PreparedPath));
    }

    [Theory]
    [InlineData(GpuJobStatus.Failed)]
    [InlineData(GpuJobStatus.Cancelled)]
    public async Task TrainedResult_ThatWasNeverInstalled_IsNeverDropped(GpuJobStatus status)
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var job = await InstalledJobAsync(f, daysAgo: 400);
        await using (var db = h.CreateContext())
        {
            // Finishing it again is the only way to that view: it stays until a newer job is installed or the photos go.
            await db.GpuJobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, status).SetProperty(j => j.InstalledAt, (DateTimeOffset?)null));
        }

        Assert.Equal(RetentionOutcome.None, await DropAsync(f, new WallCapturePipelineOptions { RetentionDryRun = false }));
        Assert.Equal(RetentionOutcome.None, await DropAsync(f, new WallCapturePipelineOptions()));
        Assert.True(Exists(f.Files, job.ResultPath!));
        Assert.True(Exists(f.Files, job.PreparedPath));
    }

    [Fact]
    public async Task AbandonedImport_IsRemoved_AnOpenOneAndForeignFoldersStay()
    {
        using var h = new WallTestHarness();
        using var s = await SupersededModelRetentionTests.ScenarioAsync(h);
        var root = Path.Combine(Path.GetDirectoryName(s.Files.ResolvePhysicalPath("probe.bin"))!, "imports");
        var abandoned = Import(root, Guid.NewGuid(), daysIdle: 4);
        var open = Import(root, Guid.NewGuid(), daysIdle: 1);
        var foreign = Directory.CreateDirectory(Path.Combine(root, "notes")).FullName;
        Backdate(foreign, 30);

        var dry = await ImportStagingRetention.RunAsync(
            s.Files, new WallCapturePipelineOptions { RetentionDryRun = true }, DateTimeOffset.UtcNow, NullLogger.Instance, CancellationToken.None);
        Assert.Equal(new RetentionOutcome(1, 1300), dry);
        Assert.True(Directory.Exists(abandoned));

        var result = await SupersededModelRetentionTests.Sweeper(s).SweepAsync(CancellationToken.None);

        Assert.Equal(new RetentionOutcome(1, 1300), result.AbandonedImports);
        Assert.False(Directory.Exists(abandoned));
        Assert.True(Directory.Exists(open));
        Assert.True(Directory.Exists(foreign));
    }

    [Fact]
    public async Task UnreferencedScenes_AreOrphans_ReferencedOnesAreNot()
    {
        using var h = new WallTestHarness();
        using var s = await SupersededModelRetentionTests.ScenarioAsync(h);
        var model = (await WallGlyphSettingsTests.Service(h).ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(), null)).Model!.Id;
        var kept = await ModelFilesTestData.AddAsync(h, s.Files, model);
        var orphan = await s.Files.SaveAsync(new byte[100], ".spz", CancellationToken.None);
        foreach (var name in kept.Append(orphan))
        {
            File.SetLastWriteTimeUtc(s.Files.ResolvePhysicalPath(name)!, DateTime.UtcNow.AddHours(-3));
        }

        var result = await SupersededModelRetentionTests.Sweeper(s).SweepAsync(CancellationToken.None);

        Assert.Equal(1, result.OrphanFiles);
        Assert.False(Exists(s.Files, orphan));
        Assert.All(kept, name => Assert.True(Exists(s.Files, name)));
    }

    [Fact]
    public async Task InADryRun_SceneOrphansStay_OtherOrphansStillGo()
    {
        using var h = new WallTestHarness();
        using var s = await SupersededModelRetentionTests.ScenarioAsync(h, dryRun: true);
        var scene = await s.Files.SaveAsync(new byte[100], ".spz", CancellationToken.None);
        var image = await s.Files.SaveAsync(CaptureScenario.TinyJpeg(3), ".jpg", CancellationToken.None);
        foreach (var name in new[] { scene, image })
        {
            File.SetLastWriteTimeUtc(s.Files.ResolvePhysicalPath(name)!, DateTime.UtcNow.AddHours(-3));
        }

        var result = await SupersededModelRetentionTests.Sweeper(s).SweepAsync(CancellationToken.None);

        Assert.Equal(1, result.OrphanFiles);
        Assert.True(Exists(s.Files, scene));
        Assert.False(Exists(s.Files, image));
    }

    [Theory]
    [InlineData(null, null, null, null, 1, 30, 3, true)]
    [InlineData("-1", "0", "7", "true", null, null, 7, true)]
    [InlineData("3", "14", "1", "false", 3, 14, 1, false)]
    [InlineData(null, null, null, "", 1, 30, 3, true)]
    [InlineData(null, null, null, "no", 1, 30, 3, true)]
    public void RetentionRules_AreSettings(
        string? keep, string? leftoverDays, string? importDays, string? dryRun, int? expectedKeep, int? expectedLeftover, int expectedImport,
        bool expectedDryRun)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blocwerk:Capture:KeepSupersededModels"] = keep,
                ["Blocwerk:Capture:RunnerLeftoverRetentionDays"] = leftoverDays,
                ["Blocwerk:Capture:ImportStagingDays"] = importDays,
                ["Blocwerk:Capture:RetentionDryRun"] = dryRun,
            })
            .Build();

        var options = WallCapturePipelineOptions.Bind(config);

        Assert.Equal(expectedKeep, options.KeepSupersededModels);
        Assert.Equal(expectedLeftover, options.RunnerLeftoverRetention?.Days);
        Assert.Equal(expectedImport, options.ImportStagingLifetime.Days);
        Assert.Equal(expectedDryRun, options.RetentionDryRun);
        Assert.Equal(14, options.SupersededModelGrace.Days);
    }

    private static Task<RetentionOutcome> DropAsync(RunnerFixture f, WallCapturePipelineOptions options) =>
        GpuJobQueue.DropAgedResultsAsync(
            f.Harness.RootContextFactory, f.Files, options, DateTimeOffset.UtcNow, NullLogger.Instance, CancellationToken.None);

    /// <summary>A job whose trained result was installed <paramref name="daysAgo"/> days ago (its leftover kept).</summary>
    private static async Task<GpuJob> InstalledJobAsync(RunnerFixture f, int daysAgo)
    {
        var job = await f.AddJobAsync(f.Harness.WallId);
        var result = await f.Files.SaveAsync(RunnerFixture.SlimPly(50), ".upl", CancellationToken.None);
        await using var db = f.Harness.CreateContext();
        var row = await db.GpuJobs.SingleAsync(j => j.Id == job.Id);
        row.Status = GpuJobStatus.Succeeded;
        row.ResultPath = result;
        row.InstalledAt = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        row.CompletedAt = row.InstalledAt;
        await db.SaveChangesAsync();
        return row;
    }

    private static string Import(string root, Guid id, int daysIdle)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, id.ToString("N"))).FullName;
        File.WriteAllBytes(Path.Combine(folder, "manifest.json"), new byte[100]);
        File.WriteAllBytes(Path.Combine(folder, "photo.jpg.part"), new byte[1200]);
        Backdate(folder, daysIdle);
        return folder;
    }

    private static void Backdate(string folder, int days)
    {
        var when = DateTime.UtcNow.AddDays(-days);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, when);
        }

        Directory.SetLastWriteTimeUtc(folder, when);
    }

    private static bool Exists(ICaptureFileStore files, string name) => File.Exists(files.ResolvePhysicalPath(name));
}
