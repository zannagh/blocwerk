// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Stage 4 (optional): the photo-real view. When a splat worker is configured (server config is the
/// opt-in; the capture only picks its <see cref="SplatQuality"/>) the capture's stored, metadata-stripped photos and the
/// active geometry go to the splat worker; <c>wall.spz</c> + <c>frame.json</c> come back and are stored
/// as a <see cref="WallGeometrySplat"/> of the model.
/// </summary>
/// <remarks>
/// Failure semantics: the model and its textures are already live, so a splat failure never fails
/// the capture. It ends as <see cref="WallCaptureStatus.SucceededWithoutSplat"/> with the reason in
/// <c>Error</c> (or stays <see cref="WallCaptureStatus.SucceededWithoutTextures"/> when textures were
/// missing too; both reasons are kept). A worker that cannot be reached at all (network error, full queue) is
/// no failure: the capture ends exactly as without a worker, with a quiet note on its follow-up record, because
/// the GPU is optional and its absence must not look like an error. While the stage runs the row is
/// <see cref="WallCaptureStatus.Splatting"/>, holding the texture outcome in <c>Error</c>, so a restart
/// resumes right here from <see cref="WallCapture.SplatJobId"/> without redoing model or textures.
/// The single capture worker is busy for the whole training run (up to an hour); captures are rare
/// admin actions, so the next one simply queues behind it. With a 3D runner (<c>WallCaptureProcessor.Runner.cs</c>)
/// the worker only prepares; the capture completes as "Model ready" and the view is added when a runner delivers it.
/// </remarks>
public sealed partial class WallCaptureProcessor
{
    /// <summary>The note a capture keeps when no photo-real worker could be reached (shown as a hint, not an error).</summary>
    internal const string NoWorkerNote =
        "The photo-real view was skipped: the GPU worker could not be reached. Everything else is done; you can retrain the photo-real view later.";

    private const string SplatKind = "splat";

    /// <summary>
    /// Ends a capture whose photo-real stage failed (or was stopped): succeeded without the splat, or
    /// still without textures when those failed first — both reasons kept in <c>Error</c>.
    /// </summary>
    internal static void EndWithoutSplat(WallCapture c, string reason)
    {
        const string polled = "Photo-real view failed: ";
        reason = reason.StartsWith(polled, StringComparison.Ordinal) ? reason[polled.Length..] : reason;
        var textureError = c.Error;
        var splatError = $"The 3D model is active, but its photo-real view could not be made: {reason}";
        var error = textureError is null ? splatError : $"{textureError} {splatError}";
        c.Status = textureError is null ? WallCaptureStatus.SucceededWithoutSplat : WallCaptureStatus.SucceededWithoutTextures;
        c.Progress = 1;
        c.Stage = textureError is null ? "Done (without the photo-real view)" : "Done (without textures)";
        c.Error = error.Length <= 2048 ? error : error[..2048];
        c.CompletedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Completes the capture, or hands it to the photo-real stage when a splat worker is configured.</summary>
    private async Task AfterTexturesAsync(CaptureRun run, string? textureError, CancellationToken ct)
    {
        if (!computeClients.Get(ComputeServiceKind.Splat).IsConfigured)
        {
            await FollowUpAsync(run, CaptureFollowUpPhase.Final, ct);
            await CompleteAsync(run.Capture.Id, TextureOutcome(textureError), textureError, ct);
            return;
        }

        // The stage gets its own restart budget: it can train for an hour, across a deploy or two.
        await UpdateAsync(run.Capture.Id, c =>
        {
            c.Status = WallCaptureStatus.Splatting;
            c.Progress = 0;
            c.Stage = "Photo-real view: sending";
            c.Error = textureError;
            c.Attempts = 1;
        }, ct);
        run.Capture.Error = textureError;
        await SplatAsync(run, ct);
    }

    private async Task SplatAsync(CaptureRun run, CancellationToken ct)
    {
        var capture = run.Capture;
        var textureError = capture.Error;
        var client = computeClients.Get(ComputeServiceKind.Splat);
        if (!client.IsConfigured || capture.GeometryModelId is not { } modelId)
        {
            await FollowUpAsync(run, CaptureFollowUpPhase.Final, ct);
            await CompleteAsync(capture.Id, TextureOutcome(textureError), textureError, ct);
            return;
        }

        SplatOutcome outcome;
        try
        {
            // The GPU half may go to a 3D runner: then the capture completes now and the view follows later.
            outcome = await SplatOnServerOrRunnerAsync(capture, modelId, client, ct);
        }
        catch (ComputeJobException ex) when (IsWorkerAbsent(ex))
        {
            if (!await WaitForFinishWorkerAsync(run, ct))
            {
                await EndWithoutWorkerAsync(run, ex, ct);
            }

            return;
        }
        catch (Exception ex) when (ex is CaptureFailedException or ComputeJobException or InvalidDataException or IOException)
        {
            logger.LogInformation("Photo-real view of capture {CaptureId} failed: {Reason}", capture.Id, ex.Message);
            if (await RestoreAfterRefinishAsync(capture.Id, ex.Message, ct))
            {
                return;
            }

            await FollowUpAsync(run, CaptureFollowUpPhase.Final, ct);
            await EndWithoutViewAsync(capture.Id, ex.Message, ct);
            return;
        }

        if (outcome == SplatOutcome.NoRunner)
        {
            await EndWithoutRunnerAsync(run, ct);
            return;
        }

        await FollowUpAsync(run, CaptureFollowUpPhase.Final, ct);
        await CompleteAsync(capture.Id, TextureOutcome(textureError), textureError, ct);
        if (outcome == SplatOutcome.Stored)
        {
            await push.NotifyWallPhotoRealReadyAsync(capture.WallId, run.User.Id);
            return;
        }

        // Model ready now; the photo-real view follows when a runner delivers it (the result may already be here).
        logger.LogInformation("Capture {CaptureId} is done; its photo-real view waits for a 3D runner", capture.Id);
        await gpuJobs!.ResumeIfDeliveredAsync(capture.Id, ct);
    }

    /// <summary>
    /// No GPU worker to be had (unreachable, or its queue full for longer than the retries): the photo-real view is
    /// optional, so this is no failure. The capture ends as if no worker were configured, with a quiet note.
    /// </summary>
    private static bool IsWorkerAbsent(ComputeJobException ex) => ex.IsTransient || ex.Kind == ComputeFailureKind.NotConfigured;

    private async Task EndWithoutWorkerAsync(CaptureRun run, ComputeJobException ex, CancellationToken ct)
    {
        var capture = run.Capture;
        logger.LogWarning(
            "Capture {CaptureId}: no photo-real worker was reachable ({Reason}); finishing without the photo-real view",
            capture.Id, ex.Message);
        await FollowUpAsync(run, CaptureFollowUpPhase.Final, ct);
        await NoteAsync(capture.Id, NoWorkerNote, ct);
        await CompleteAsync(capture.Id, TextureOutcome(capture.Error), capture.Error, ct);
    }

    private static WallCaptureStatus TextureOutcome(string? textureError) =>
        textureError is null ? WallCaptureStatus.Succeeded : WallCaptureStatus.SucceededWithoutTextures;

    /// <summary>The all-in-one path: the splat worker trains itself (no runners involved).</summary>
    private async Task SplatAllInOneAsync(WallCapture capture, Guid modelId, IComputeJobClient client, CancellationToken ct)
    {
        if (capture.SplatJobId is null)
        {
            await PrepareVideoFramesAsync(capture.Id, ct);
        }

        var status = await RunJobAsync(
            capture.SplatJobId,
            () => SubmitSplatAsync(capture.Id, modelId, capture.SplatQuality, client, ct),
            jobId => UpdateAsync(capture.Id, c => c.SplatJobId = jobId, ct),
            client,
            new JobStage(capture.Id, WallCaptureStatus.Splatting, VideoBand, 0.98, "Photo-real view", CaptureSplatDocuments.Describe),
            ct);
        await StoreSplatAsync(capture.Id, modelId, status.JobId!, client, ct);
        await DropRunnerLeftoversAsync(capture.Id, ct);
    }

    private async Task<string> SubmitSplatAsync(
        Guid captureId, Guid modelId, SplatQuality? quality, IComputeJobClient client, CancellationToken ct, string jobKind = SplatKind)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var geometry = await db.WallGeometryModels.Where(m => m.Id == modelId).Select(m => m.Json).FirstAsync(ct);
        var parts = new List<ComputeJobPart> { ComputeJobPart.Json("geometry", geometry) };
        using var photoParts = new ComputePhotoParts(files);
        parts.AddRange(await PhotoPartsAsync(photoParts, await LoadUsablePhotosAsync(captureId, ct), CaptureComputeDocuments.PhotoName, ct));

        // The walk-along video's frames (if any): auxiliary images for coverage, never for alignment.
        parts.AddRange(await FramePartsAsync(photoParts, captureId, ct));
        parts.Add(ComputeJobPart.Json("options", CaptureSplatDocuments.BuildOptions(settings.SplatMaxSteps, quality)));
        return await client.SubmitMultipartAsync(jobKind, parts, ct);
    }

    /// <summary>
    /// Stored photos as <c>photos</c> file parts, streamed from disk and metadata stripped (<see cref="ComputePhotoParts"/>):
    /// no GPS, no camera serials, no orientation. The stem (<paramref name="name"/> of the photo index) is the camera name
    /// the solve used, so the worker can align to it.
    /// </summary>
    private static async Task<List<ComputeJobPart>> PhotoPartsAsync(
        ComputePhotoParts photoParts, IEnumerable<WallCapturePhoto> photos, Func<int, string> name, CancellationToken ct)
    {
        var parts = new List<ComputeJobPart>();
        foreach (var photo in photos)
        {
            parts.Add(await photoParts.PartAsync("photos", name(photo.Index), photo.StoredPath, asJpeg: false, ct)
                      ?? throw new CaptureFailedException($"Photo {photo.Index} is missing on the server."));
        }

        return parts;
    }
}
