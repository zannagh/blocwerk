// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Rendering a finished capture's wall textures again (<see cref="WallCaptureService.RerenderTexturesAsync"/>), beside
/// the capture worker (<see cref="WallTextureRerenderWorker"/>): only the textures job for the capture's active model,
/// tracked on <see cref="WallCapture.TexturesJobId"/> (<see cref="CaptureTextureOutcome.RerenderMark"/>). The capture's
/// status, stage and photo-real state (splat job, GPU jobs) are never touched, so a running photo-real stage goes on;
/// only the texture half of <c>Error</c> changes. New textures re-place the holds (the chain's step that reads them); after a
/// re-solve (<see cref="CaptureFollowUpRecord.Rederive"/>) every other follow-up then runs again too.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    /// <summary>Runs the capture's pending texture re-render (no-op without one). Cancellation leaves it to resume.</summary>
    public Task RerenderTexturesAsync(Guid captureId, CancellationToken ct) =>
        RunOnceAsync(rerendering, captureId, () => RerenderOnceAsync(captureId, ct));

    private async Task RerenderOnceAsync(Guid captureId, CancellationToken ct)
    {
        if (await RerenderTargetAsync(captureId, ct) is not { } target)
        {
            return;
        }

        string? jobId;
        try
        {
            jobId = await RenderTexturesAgainAsync(captureId, target.ModelId, target.JobId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Rendering the textures of capture {CaptureId} again failed", captureId);
            var reason = ex switch
            {
                DbUpdateException => "they could not be saved.",
                CaptureFailedException or ComputeJobException or InvalidDataException or IOException => ex.Message,
                _ => "something went wrong on the server.",
            };
            await EndRerenderAsync(captureId, target.ModelId, null, $"The 3D model is active, but its textures could not be made: {reason}", ct);
            return;
        }

        await EndRerenderAsync(captureId, target.ModelId, jobId, null, ct);
        logger.LogInformation("Textures of capture {CaptureId} rendered again (job {JobId})", captureId, jobId);
        if (followUps is not null)
        {
            await followUps.RerunAsync(captureId, PlaceHoldsFollowUpStep.StepKey, ct);
            await followUps.RunMissingAsync(captureId, ct);
        }
    }

    private async Task<string> RenderTexturesAgainAsync(Guid captureId, Guid modelId, string? existingJobId, CancellationToken ct)
    {
        var client = computeClients.Get(ComputeServiceKind.Geometry);
        if (!client.IsConfigured)
        {
            throw new CaptureFailedException("the 3D computation service is not configured on this server.");
        }

        var status = await RunJobAsync(
            existingJobId,
            () => SubmitTexturesAsync(captureId, modelId, client, ct),
            jobId => UpdateAsync(captureId, c => c.TexturesJobId = CaptureTextureOutcome.RerenderMark + jobId, ct),
            client,
            new JobStage(captureId, WallCaptureStatus.Texturing, 0, 1, "Rendering wall textures", Silent: true),
            ct);
        await StoreTexturesAsync(captureId, modelId, status, client, ct, silent: true);
        return status.JobId!;
    }

    /// <summary>The model and the submitted job of a pending re-render; null (and the mark dropped) when it may not run.</summary>
    private async Task<(Guid ModelId, string? JobId)?> RerenderTargetAsync(Guid captureId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var capture = await db.WallCaptures.AsNoTracking().FirstOrDefaultAsync(c => c.Id == captureId, ct);
        if (capture is null || !CaptureTextureOutcome.IsRerendering(capture.TexturesJobId))
        {
            return null;
        }

        var modelId = capture.GeometryModelId;
        var active = modelId is not null
            && await db.WallGeometryModels.AnyAsync(m => m.Id == modelId && m.WallId == capture.WallId && m.IsActive, ct);
        if (!active || !WallCaptureService.MayRerenderTextures(capture.Status))
        {
            logger.LogInformation("Capture {CaptureId}: its textures are not rendered again (its model is no longer active)", captureId);
            await ClearMarkAsync(captureId, c => c.TexturesJobId = null, ct);
            return null;
        }

        return (modelId!.Value, CaptureTextureOutcome.RerenderJobId(capture.TexturesJobId));
    }

    /// <summary>
    /// Records the outcome on the row as it is now (the photo-real stage may have moved it on meanwhile). A failed
    /// refresh of textures that still exist keeps them and says so in a note instead of calling the model untextured.
    /// </summary>
    private async Task EndRerenderAsync(Guid captureId, Guid modelId, string? jobId, string? textureError, CancellationToken ct)
    {
        bool kept;
        await using (var db = dbContextFactory.CreateDbContext())
        {
            kept = textureError is not null && await db.WallGeometryTextures.AnyAsync(t => t.GeometryModelId == modelId, ct);
        }

        await ClearMarkAsync(
            captureId,
            c =>
            {
                c.TexturesJobId = jobId;
                if (kept)
                {
                    AddFollowUpNote(c, $"Rendering the wall textures again failed ({textureError}); the previous textures stay.");
                    return;
                }

                CaptureTextureOutcome.Apply(c, textureError);
            },
            ct);
    }
}
