// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Finishing a capture's trained photo-real view again, without training (<see cref="Runners.GpuJobQueue"/>'s refinish):
/// the capture goes back to <see cref="WallCaptureStatus.Splatting"/> with its newest GPU job reopened as delivered, so the
/// pipeline re-runs the server half (crop, clean-up, export, level-of-detail ladder), installs the view and runs the
/// whole post-capture chain again (its record is cleared once the view is installed; a failed re-finish restores the
/// job and the capture as they were). For trying changes to those steps in minutes.
/// </summary>
public sealed partial class WallCaptureService
{
    public async Task<IReadOnlyList<string>> RefinishPhotoRealAsync(Guid captureId)
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

            var job = await Runners.GpuJobQueue.RefinishSourceAsync(db, files, capture.Id, CancellationToken.None);
            if (job is null)
            {
                return ["This capture has no trained photo-real view left on the server to finish again (a 3D runner's view is kept until a newer one is installed)."];
            }

            Runners.GpuJobQueue.ReopenForRefinish(job, capture, DateTimeOffset.UtcNow);
            capture.Error = capture.Status == WallCaptureStatus.SucceededWithoutTextures ? TexturePart(capture.Error) : null;
            capture.Status = WallCaptureStatus.Splatting;
            capture.Stage = "Photo-real view: finishing the trained view again (no new training)";
            capture.Progress = 0.96;
            capture.Attempts = 0;
            capture.CompletedAt = null;
            await db.SaveChangesAsync();
            queue.Enqueue(capture.Id);
            logger.LogInformation(
                "Photo-real view of capture {CaptureId} queued to be finished again from GPU job {JobId} by {UserId}", capture.Id, job.Id, userId);
            return [];
        }
    }
}
