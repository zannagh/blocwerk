// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Retraining a finished capture's photo-real view at another <see cref="SplatQuality"/>: the capture
/// goes back to <see cref="WallCaptureStatus.Splatting"/> with no splat job, so the pipeline's resume
/// path submits a new one from the stored photos and video frames. The current splat stays live until
/// the new one is stored (<c>StoreSplatAsync</c> replaces it only on success); a failed retrain ends
/// the capture as before, without touching it.
/// </summary>
public sealed partial class WallCaptureService
{
    /// <summary>How <c>EndWithoutSplat</c> starts the splat half of a finished capture's error.</summary>
    private const string SplatErrorStart = "The 3D model is active, but its photo-real view could not be made:";

    public async Task<IReadOnlyList<string>> RetrainPhotoRealAsync(Guid captureId, SplatQuality quality)
    {
        if (!computeClients.Get(ComputeServiceKind.Splat).IsConfigured)
        {
            return ["No photo-real (splat) worker is configured on this server."];
        }

        var (db, userId, capture) = await OpenCaptureAsync(captureId);
        await using (db)
        {
            var problems = await RetrainProblemsAsync(db, capture);
            if (problems.Count > 0)
            {
                return problems;
            }

            var textureError = capture.Status == WallCaptureStatus.SucceededWithoutTextures ? TexturePart(capture.Error) : null;
            capture.SplatQuality = quality;
            capture.SplatJobId = null;

            // A markerless capture's reconstruction was prepared at the old quality: the retrain takes the normal route.
            capture.SfmJobId = null;
            capture.Status = WallCaptureStatus.Splatting;
            capture.Stage = $"Photo-real view: waiting to retrain ({CaptureSplatDocuments.QualityName(quality)} quality)";
            capture.Progress = 0;
            capture.Error = textureError;
            capture.Attempts = 0;
            capture.CompletedAt = null;
            capture.FollowUpJson = WithoutNote(capture.FollowUpJson);

            // A photo-real view still waiting for (or on) a 3D runner is superseded by the new one.
            var superseded = await Runners.GpuJobQueue.CancelActiveAsync(
                db, capture.Id, "superseded by a retrain", DateTimeOffset.UtcNow, CancellationToken.None);
            await db.SaveChangesAsync();
            foreach (var path in superseded.SelectMany(j => new[] { j.BundlePath, j.PreparedPath, j.ResultPath }))
            {
                files.Delete(path);
            }

            queue.Enqueue(capture.Id);
            logger.LogInformation(
                "Photo-real view of capture {CaptureId} queued for retraining at {Quality} by {UserId}", capture.Id, quality, userId);
            return [];
        }
    }

    /// <summary>The texture half of a finished capture's error (the splat half is about to be redone).</summary>
    internal static string? TexturePart(string? error)
    {
        if (error is null)
        {
            return null;
        }

        var i = error.IndexOf(SplatErrorStart, StringComparison.Ordinal);
        var part = (i < 0 ? error : error[..i]).Trim();
        return part.Length == 0 ? null : part;
    }

    /// <summary>The follow-up record without its note (the note spoke about the photo-real view being redone).</summary>
    private static string? WithoutNote(string? followUpJson) => followUpJson is null
        ? null
        : (CaptureFollowUpRecord.Parse(followUpJson) with { Note = null }).ToJson();

    private static async Task<List<string>> RetrainProblemsAsync(Data.BlocwerkDbContext db, WallCapture capture)
    {
        var errors = new List<string>();
        if (capture.Status is not (WallCaptureStatus.Succeeded or WallCaptureStatus.SucceededWithoutSplat
            or WallCaptureStatus.SucceededWithoutTextures))
        {
            errors.Add("Only a finished capture can be retrained.");
            return errors;
        }

        if (capture.GeometryModelId is not { } modelId || !await db.WallGeometryModels.AnyAsync(m => m.Id == modelId))
        {
            errors.Add("This capture's 3D model no longer exists.");
        }

        if (await db.WallCapturePhotos.CountAsync(p => p.CaptureId == capture.Id) < 2)
        {
            errors.Add("This capture's photos were already deleted (they are kept for a limited time). Start a new capture.");
        }

        var busy = await db.WallCaptures.AnyAsync(c => c.WallId == capture.WallId && c.Id != capture.Id
            && (c.Status == WallCaptureStatus.Queued || c.Status == WallCaptureStatus.Detecting || c.Status == WallCaptureStatus.Solving
                || c.Status == WallCaptureStatus.Texturing || c.Status == WallCaptureStatus.Splatting));
        if (busy)
        {
            errors.Add("Another capture of this wall is still being processed. Wait for it to finish.");
        }

        // The pipeline runs as the capture's creator (and re-checks that at every pickup).
        if (!await WallAdminGuard.IsWallAdminAsync(db, capture.WallId, capture.CreatedByUserId, CancellationToken.None))
        {
            errors.Add("The admin who started this capture no longer administers the wall; start a new capture instead.");
        }

        return errors;
    }
}
