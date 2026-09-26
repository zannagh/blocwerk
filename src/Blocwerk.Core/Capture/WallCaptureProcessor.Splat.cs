// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text;
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
            await EndWithoutWorkerAsync(run, ex, ct);
            return;
        }
        catch (Exception ex) when (ex is CaptureFailedException or ComputeJobException or InvalidDataException or IOException)
        {
            logger.LogInformation("Photo-real view of capture {CaptureId} failed: {Reason}", capture.Id, ex.Message);
            await FollowUpAsync(run, CaptureFollowUpPhase.Final, ct);
            await UpdateAsync(capture.Id, c => EndWithoutSplat(c, ex.Message), ct);
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
        await UpdateAsync(capture.Id, c => AddFollowUpNote(c, NoWorkerNote), ct);
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
    }

    private async Task<string> SubmitSplatAsync(
        Guid captureId, Guid modelId, SplatQuality? quality, IComputeJobClient client, CancellationToken ct, string jobKind = SplatKind)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var geometry = await db.WallGeometryModels.Where(m => m.Id == modelId).Select(m => m.Json).FirstAsync(ct);
        var parts = new List<ComputeJobPart> { ComputeJobPart.Json("geometry", geometry) };
        parts.AddRange(await PhotoPartsAsync(await LoadPhotosAsync(captureId, ct), CaptureComputeDocuments.PhotoName, ct));

        // The walk-along video's frames (if any): auxiliary images for coverage, never for alignment.
        parts.AddRange(await FramePartsAsync(captureId, ct));
        parts.Add(ComputeJobPart.Json("options", CaptureSplatDocuments.BuildOptions(settings.SplatMaxSteps, quality)));
        return await client.SubmitMultipartAsync(jobKind, parts, ct);
    }

    /// <summary>
    /// Stored photos as <c>photos</c> file parts, metadata stripped: no GPS, no camera serials, no orientation. The stem
    /// (<paramref name="name"/> of the photo index) is the camera name the solve used, so the worker can align to it.
    /// </summary>
    private async Task<List<ComputeJobPart>> PhotoPartsAsync(
        IEnumerable<WallCapturePhoto> photos, Func<int, string> name, CancellationToken ct)
    {
        var parts = new List<ComputeJobPart>();
        foreach (var photo in photos)
        {
            var bytes = await files.ReadAsync(photo.StoredPath, ct)
                        ?? throw new CaptureFailedException($"Photo {photo.Index} is missing on the server.");
            var clean = ImageMetadataStripper.Strip(bytes);
            var kind = CapturePhotoFormat.Sniff(clean);
            parts.Add(ComputeJobPart.File("photos", name(photo.Index) + CapturePhotoFormat.Extension(kind), clean, CapturePhotoFormat.ContentType(kind)));
        }

        return parts;
    }

    private async Task StoreSplatAsync(Guid captureId, Guid modelId, string jobId, IComputeJobClient client, CancellationToken ct)
    {
        await SetStageAsync(captureId, WallCaptureStatus.Splatting, 0.99, "Photo-real view: saving", ct);
        var frameJson = Encoding.UTF8.GetString(await client.DownloadFileAsync(jobId, CaptureSplatDocuments.FrameFile, ct));
        CaptureSplatDocuments.Validate(frameJson);
        var spz = await client.DownloadFileAsync(jobId, CaptureSplatDocuments.SpzFile, ct);

        // .spz is a gzip stream (Niantic SPZ v2).
        if (spz.Length < 32 || spz[0] != 0x1f || spz[1] != 0x8b)
        {
            throw new InvalidDataException("the photo-real scene is not an .spz file.");
        }

        // The level-of-detail ladder (SplatLodLadder): the view starts small and steps up while the
        // device keeps up, so a phone never has to survive the full scene. Supersedes the mobile copy.
        var (count, levels) = await LevelsOfDetailAsync(spz, modelId, ct);
        var uncleaned = await DownloadUncleanedAsync(frameJson, jobId, client, modelId, ct);
        var row = new WallGeometrySplat
        {
            GeometryModelId = modelId,
            StoredPath = await files.SaveAsync(spz, ".spz", ct),
            SizeBytes = spz.LongLength,
            SplatCount = count,
            LodLevelsJson = SplatLodLadder.Serialize(levels),
            UncleanedStoredPath = uncleaned is null ? null : await files.SaveAsync(uncleaned, ".spz", ct),
            UncleanedSizeBytes = uncleaned?.LongLength,
            FrameJson = frameJson,
        };

        await using var db = dbContextFactory.CreateDbContext();
        var old = await db.WallGeometrySplats.Where(s => s.GeometryModelId == modelId).ToListAsync(ct);
        db.WallGeometrySplats.RemoveRange(old);
        db.WallGeometrySplats.Add(row);
        await db.SaveChangesAsync(ct);
        foreach (var file in old.SelectMany(SplatLodLadder.Files))
        {
            files.Delete(file);
        }

        logger.LogInformation(
            "Stored the photo-real view of model {ModelId} ({Bytes} bytes, alignment residual {Residual})",
            modelId, row.SizeBytes, CaptureSplatDocuments.ResidualText(frameJson));
    }

    /// <summary>
    /// The scene as trained, before the worker's floater clean-up (kept so the clean-up can be
    /// reverted), or null when the worker did not clean it. A failed download only loses that copy.
    /// </summary>
    private async Task<byte[]?> DownloadUncleanedAsync(
        string frameJson, string jobId, IComputeJobClient client, Guid modelId, CancellationToken ct)
    {
        if (CaptureSplatDocuments.UncleanedFile(frameJson) is not { } name)
        {
            return null;
        }

        try
        {
            var bytes = await client.DownloadFileAsync(jobId, name, ct);
            return bytes.Length >= 32 && bytes[0] == 0x1f && bytes[1] == 0x8b ? bytes : null;
        }
        catch (ComputeJobException ex)
        {
            logger.LogWarning("No uncleaned copy of the photo-real view of model {ModelId}: {Reason}", modelId, ex.Message);
            return null;
        }
    }

    /// <summary>The ladder's levels (saved), or none (small scene, or a layout the pruner does not read).</summary>
    private async Task<(int? Count, List<SplatLodLevel> Levels)> LevelsOfDetailAsync(byte[] spz, Guid modelId, CancellationToken ct)
    {
        try
        {
            return await SplatLodBackfill.SaveLadderAsync(spz, files, ct);
        }
        catch (InvalidDataException ex)
        {
            // The full scene still works everywhere a desktop GPU is; phones just get it whole.
            logger.LogWarning("No level-of-detail ladder for the photo-real view of model {ModelId}: {Reason}", modelId, ex.Message);
            return (null, []);
        }
    }
}
