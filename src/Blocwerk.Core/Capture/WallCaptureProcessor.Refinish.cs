// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Runners;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The capture side of a re-finish (<see cref="WallCaptureService.RefinishPhotoRealAsync"/>) and of the runner leftovers:
/// a failed re-finish puts the capture back as it was (the view before is still installed) with a note, and a view the
/// splat worker trained itself drops the capture's runner leftovers, so no re-finish can bring an older view back.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    /// <summary>True when the failed photo-real stage was a re-finish, now restored.</summary>
    private async Task<bool> RestoreAfterRefinishAsync(Guid captureId, string reason, CancellationToken ct)
    {
        if (gpuJobs is null || await gpuJobs.RestoreRefinishAsync(captureId, ct) is not { } before)
        {
            return false;
        }

        logger.LogInformation("Re-finishing the photo-real view of capture {CaptureId} failed; restored as it was: {Reason}", captureId, reason);
        await UpdateAsync(
            captureId,
            c =>
            {
                var textureError = c.Error;
                c.Status = before.CaptureStatus;
                c.Error = before.CaptureError;
                c.Stage = before.CaptureStage;

                // Textures rendered again meanwhile (RerenderTexturesAsync) keep their outcome.
                if (WallCaptureService.TexturePart(before.CaptureError) != textureError)
                {
                    CaptureTextureOutcome.Apply(c, textureError);
                }

                c.CompletedAt = before.CaptureCompletedAt ?? DateTimeOffset.UtcNow;
                c.Progress = 1;
            },
            ct);
        await NoteAsync(captureId, $"Finishing the trained photo-real view again failed ({reason}); the view installed before stays.", ct);
        return true;
    }

    /// <summary>Drops the capture's runner leftovers (a newer view, trained by the splat worker, is installed now).</summary>
    private async Task DropRunnerLeftoversAsync(Guid captureId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var spent = await GpuJobQueue.DropLeftoversAsync(db, [captureId], null, ct);
        if (spent.Count == 0)
        {
            return;
        }

        await db.SaveChangesAsync(ct);
        foreach (var path in spent)
        {
            files.Delete(path);
        }
    }
}
