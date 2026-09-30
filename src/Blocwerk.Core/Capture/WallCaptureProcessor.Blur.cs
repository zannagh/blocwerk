// <copyright file="WallCaptureProcessor.Blur.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// After detection: every photo has a sharpness score (uploads score on the way in; older ones, and the
/// bogus 0 of photos scored before the scorer read colour JPEGs, are scored here: <see cref="CapturePhotoSharpness"/>), and the
/// clearly blurry photos without a decoded marker are flagged <see cref="WallCapturePhoto.ExcludedBlurry"/>
/// (<see cref="CaptureBlurFilter"/>), so feature matching and training never see them. The marker solve is unaffected:
/// a photo it can use has markers and is never flagged.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    private async Task MarkBlurryPhotosAsync(CaptureRun run, CancellationToken ct)
    {
        var captureId = run.Capture.Id;
        if (run.Capture.SolveJobId is not null || run.Capture.SfmJobId is not null)
        {
            return;
        }

        var photos = await LoadPhotosAsync(captureId, ct);
        await ScoreMissingSharpnessAsync(photos, ct);
        var blurry = CaptureBlurFilter.Blurry(photos, options.BlurExcludeRatio).ToList();
        await using (var db = dbContextFactory.CreateDbContext())
        {
            await db.WallCapturePhotos
                .Where(p => p.CaptureId == captureId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ExcludedBlurry, p => blurry.Contains(p.Index)), ct);
        }

        if (blurry.Count > 0)
        {
            logger.LogInformation(
                "Capture {CaptureId}: {Blurry} of {Photos} photo(s) left out as blurry (no marker decoded in them)",
                captureId, blurry.Count, photos.Count);
            await SetStageAsync(
                captureId, WallCaptureStatus.Detecting, 0.2, $"{blurry.Count} of {photos.Count} photos left out as too blurry", ct);
        }
    }

    /// <summary>Scores the photos without a real score: null, or the 0 stored before the scorer read colour JPEGs.</summary>
    private async Task ScoreMissingSharpnessAsync(List<WallCapturePhoto> photos, CancellationToken ct)
    {
        foreach (var photo in photos.Where(p => !CapturePhotoSharpness.IsScored(p.Sharpness)))
        {
            if (!await CapturePhotoSharpness.ScoreIfMissingAsync(photo, files, options.PhotoSharpnessEdge, ct))
            {
                continue;
            }

            var score = photo.Sharpness;
            await using var db = dbContextFactory.CreateDbContext();
            await db.WallCapturePhotos
                .Where(p => p.Id == photo.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Sharpness, score), ct);
        }
    }
}
