// <copyright file="CapturePhotoSharpnessTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Regression: before the scorer decoded colour JPEGs every capture photo was stored with sharpness 0, and only a missing
/// score was ever computed, so the 0 stayed. A 0 now counts as unscored: the startup backfill, the pipeline's blur step and
/// the replay package score it again, and the blur filter never judges it.
/// </summary>
public class CapturePhotoSharpnessTests
{
    [Fact]
    public void Zero_IsNotAScore()
    {
        Assert.False(CapturePhotoSharpness.IsScored(null));
        Assert.False(CapturePhotoSharpness.IsScored(0));
        Assert.True(CapturePhotoSharpness.IsScored(12.5));
        Assert.Null(CapturePhotoSharpness.Stored(0));
        Assert.Equal(12.5, CapturePhotoSharpness.Stored(12.5));
    }

    [Fact]
    public async Task TheBackfill_ScoresTheBogusZeros_AndLeavesAPhotoWithoutItsFileUnscored()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync(photos: 3);
        await WithBogusZerosAsync(h, s.Files, captureId);
        await using (var db = h.CreateContext())
        {
            s.Files.Delete((await db.WallCapturePhotos.SingleAsync(p => p.CaptureId == captureId && p.Index == 3)).StoredPath);
        }

        var backfill = new CapturePhotoSharpnessBackfill(
            h.RootContextFactory, s.Files, s.Options, NullLogger<CapturePhotoSharpnessBackfill>.Instance) { Pause = TimeSpan.Zero };

        Assert.Equal(2, await backfill.RunAsync(CancellationToken.None));
        await using var check = h.CreateContext();
        var scores = await check.WallCapturePhotos.OrderBy(p => p.Index).Select(p => p.Sharpness).ToListAsync();
        Assert.All(scores.Take(2), x => Assert.True(x > 0, $"score {x}"));
        Assert.Null(scores[2]);
        Assert.Equal(0, await backfill.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ThePackage_CarriesRealScores_EvenFromAnExportOrPackageWithTheBogusZeros()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        await WithBogusZerosAsync(h, f.Runner.Scenario.Files, f.CaptureId);

        var (manifest, bytes) = await f.ExportAsync();
        Assert.All(manifest.Rows.Photos, p => Assert.True(p.Sharpness > 0, $"exported {p.Sharpness}"));

        // a package exported before the fix: the import scores the photos from their files
        await f.ForgetAsync(manifest);
        foreach (var photo in manifest.Rows.Photos)
        {
            photo.Sharpness = 0;
        }

        await f.Service.BeginImportAsync(manifest, CancellationToken.None);
        await f.UploadAllAsync(manifest, bytes);
        Assert.True((await f.Service.CommitImportAsync(f.CaptureId, CancellationToken.None)).Committed);
        await using var db = h.CreateContext();
        Assert.All(await db.WallCapturePhotos.Select(p => p.Sharpness).ToListAsync(), x => Assert.True(x > 0, $"imported {x}"));
    }

    /// <summary>The capture's photo files become real colour photos, each stored with the pre-fix score 0.</summary>
    private static async Task WithBogusZerosAsync(WallTestHarness h, ICaptureFileStore files, Guid captureId)
    {
        await using var db = h.CreateContext();
        foreach (var path in await db.WallCapturePhotos.Where(p => p.CaptureId == captureId).Select(p => p.StoredPath).ToListAsync())
        {
            await File.WriteAllBytesAsync(files.ResolvePhysicalPath(path)!, CaptureFrameSharpnessTests.ColourJpeg(blurSigma: 0));
        }

        await db.WallCapturePhotos.Where(p => p.CaptureId == captureId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Sharpness, 0.0));
    }
}
