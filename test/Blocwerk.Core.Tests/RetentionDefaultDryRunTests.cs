// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Deleting old models' 3D files, runner results and abandoned imports is opt-in: with the default settings (and with
/// an unset or empty <c>CAPTURE__RETENTIONDRYRUN</c>) a sweep only reports what all three rules would free.
/// </summary>
public class RetentionDefaultDryRunTests
{
    [Fact]
    public async Task WithTheDefaultSettings_NothingIsDeleted_ButEveryRuleReportsWhatItWouldFree()
    {
        using var h = new WallTestHarness();
        using var s = await SupersededModelRetentionTests.ScenarioAsync(h);
        s.Options = WallCapturePipelineOptions.Bind(new ConfigurationBuilder().Build());
        Assert.True(s.Options.RetentionDryRun);
        Assert.True(new WallCapturePipelineOptions().RetentionDryRun);

        var glyphs = WallGlyphSettingsTests.Service(h);
        var old = (await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(), null)).Model!.Id;
        var previous = (await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(), null)).Model!.Id;
        await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(), null);
        var modelFiles = await ModelFilesTestData.AddAsync(h, s.Files, old);
        var (jobId, runnerFiles) = await AddAgedRunnerResultAsync(h, s.Files);
        await using (var db = h.CreateContext())
        {
            await db.WallGeometryModels.Where(m => m.Id == old)
                .ExecuteUpdateAsync(x => x.SetProperty(m => m.RetiredAt, DateTimeOffset.UtcNow.AddDays(-90)));
            await db.WallGeometryModels.Where(m => m.Id == previous)
                .ExecuteUpdateAsync(x => x.SetProperty(m => m.RetiredAt, DateTimeOffset.UtcNow.AddDays(-60)));
        }

        var import = Path.Combine(Path.GetDirectoryName(s.Files.ResolvePhysicalPath("probe.bin"))!, "imports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(import);
        File.WriteAllBytes(Path.Combine(import, "manifest.json"), new byte[100]);
        File.SetLastWriteTimeUtc(Path.Combine(import, "manifest.json"), DateTime.UtcNow.AddDays(-10));
        Directory.SetLastWriteTimeUtc(import, DateTime.UtcNow.AddDays(-10));

        var result = await new WallCaptureSweeper(h.RootContextFactory, s.Files, s.Options, NullLogger<WallCaptureSweeper>.Instance)
            .SweepAsync(CancellationToken.None);

        // Every rule found its candidate...
        Assert.Equal(1, result.SupersededModels.Count);
        Assert.Equal(1, result.RunnerResults.Count);
        Assert.Equal(1, result.AbandonedImports.Count);
        Assert.True(result.SupersededModels.Bytes > 0 && result.RunnerResults.Bytes > 0 && result.AbandonedImports.Bytes > 0);

        // ...and deleted nothing.
        Assert.Equal(0, result.FreedBytes);
        Assert.All(modelFiles.Concat(runnerFiles), f => Assert.True(File.Exists(s.Files.ResolvePhysicalPath(f)), f));
        Assert.True(Directory.Exists(import));
        await using var check = h.CreateContext();
        Assert.True(await check.WallGeometryTextures.AnyAsync(t => t.GeometryModelId == old));
        Assert.True(await check.WallGeometrySplats.AnyAsync(t => t.GeometryModelId == old));
        Assert.False(await check.WallGeometryModels.AnyAsync(m => m.FilesRemovedAt != null));
        Assert.NotNull((await check.GpuJobs.SingleAsync(j => j.Id == jobId)).ResultPath);
    }

    /// <summary>A capture whose runner job's trained result was installed 90 days ago (result and prepared state kept).</summary>
    private static async Task<(Guid JobId, List<string> Files)> AddAgedRunnerResultAsync(WallTestHarness h, ICaptureFileStore files)
    {
        var ct = CancellationToken.None;
        await using var db = h.CreateContext();
        var capture = new WallCapture { WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallCaptureStatus.Succeeded, Stage = "Done" };
        db.WallCaptures.Add(capture);
        var installed = DateTimeOffset.UtcNow.AddDays(-90);
        var job = new GpuJob
        {
            WallId = h.WallId, CaptureId = capture.Id, GeometryModelId = Guid.NewGuid(), Quality = SplatQuality.Draft,
            BundlePath = await files.SaveAsync([1], ".zip", ct), BundleSha256 = new string('0', 64),
            PreparedPath = await files.SaveAsync("{}"u8.ToArray(), ".prep", ct),
            ResultPath = await files.SaveAsync(RunnerFixture.SlimPly(50), ".upl", ct),
            Status = GpuJobStatus.Succeeded, InstalledAt = installed, CompletedAt = installed,
        };
        db.GpuJobs.Add(job);
        await db.SaveChangesAsync();
        return (job.Id, [job.PreparedPath, job.ResultPath]);
    }
}
