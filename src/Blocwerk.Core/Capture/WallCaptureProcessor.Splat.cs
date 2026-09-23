// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Stage 4 (optional): the photo-real view. When a splat worker is configured (server config is the
/// opt-in; there is no per-capture switch) the capture's stored, metadata-stripped photos and the
/// active geometry go to the splat worker; <c>wall.spz</c> + <c>frame.json</c> come back and are stored
/// as a <see cref="WallGeometrySplat"/> of the model.
/// </summary>
/// <remarks>
/// Failure semantics: the model and its textures are already live, so a splat failure never fails
/// the capture. It ends as <see cref="WallCaptureStatus.SucceededWithoutSplat"/> with the reason in
/// <c>Error</c> (or stays <see cref="WallCaptureStatus.SucceededWithoutTextures"/> when textures were
/// missing too; both reasons are kept). While the stage runs the row is
/// <see cref="WallCaptureStatus.Splatting"/>, holding the texture outcome in <c>Error</c>, so a restart
/// resumes right here from <see cref="WallCapture.SplatJobId"/> without redoing model or textures.
/// The single capture worker is busy for the whole training run (up to an hour); captures are rare
/// admin actions, so the next one simply queues behind it.
/// </remarks>
public sealed partial class WallCaptureProcessor
{
    private const string SplatKind = "splat";

    /// <summary>Completes the capture, or hands it to the photo-real stage when a splat worker is configured.</summary>
    private async Task AfterTexturesAsync(CaptureRun run, string? textureError, CancellationToken ct)
    {
        if (!computeClients.Get(ComputeServiceKind.Splat).IsConfigured)
        {
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
            await CompleteAsync(capture.Id, TextureOutcome(textureError), textureError, ct);
            return;
        }

        try
        {
            if (capture.SplatJobId is null)
            {
                await PrepareVideoFramesAsync(capture.Id, ct);
            }

            var status = await RunJobAsync(
                capture.SplatJobId,
                () => SubmitSplatAsync(capture.Id, modelId, client, ct),
                jobId => UpdateAsync(capture.Id, c => c.SplatJobId = jobId, ct),
                client,
                new JobStage(capture.Id, WallCaptureStatus.Splatting, VideoBand, 0.98, "Photo-real view", CaptureSplatDocuments.Describe),
                ct);
            await StoreSplatAsync(capture.Id, modelId, status.JobId!, client, ct);
        }
        catch (Exception ex) when (ex is CaptureFailedException or ComputeJobException or InvalidDataException or IOException)
        {
            logger.LogInformation("Photo-real view of capture {CaptureId} failed: {Reason}", capture.Id, ex.Message);
            await UpdateAsync(capture.Id, c => EndWithoutSplat(c, ex.Message), ct);
            return;
        }

        await CompleteAsync(capture.Id, TextureOutcome(textureError), textureError, ct);
        await push.NotifyWallPhotoRealReadyAsync(capture.WallId, run.User.Id);
    }

    /// <summary>
    /// Ends a capture whose photo-real stage failed (or was stopped): succeeded without the splat, or
    /// still without textures when those failed first — both reasons kept in <c>Error</c>.
    /// </summary>
    private static void EndWithoutSplat(WallCapture c, string reason)
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

    private static WallCaptureStatus TextureOutcome(string? textureError) =>
        textureError is null ? WallCaptureStatus.Succeeded : WallCaptureStatus.SucceededWithoutTextures;

    private async Task<string> SubmitSplatAsync(Guid captureId, Guid modelId, IComputeJobClient client, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var geometry = await db.WallGeometryModels.Where(m => m.Id == modelId).Select(m => m.Json).FirstAsync(ct);
        var parts = new List<ComputeJobPart> { ComputeJobPart.Json("geometry", geometry) };
        foreach (var photo in await LoadPhotosAsync(captureId, ct))
        {
            var bytes = await files.ReadAsync(photo.StoredPath, ct)
                        ?? throw new CaptureFailedException($"Photo {photo.Index} is missing on the server.");

            // Nothing leaves this server with metadata: no GPS, no camera serials, no orientation.
            // The file name stem is the camera name the solve used, so the worker can align to it.
            var clean = ImageMetadataStripper.Strip(bytes);
            var kind = CapturePhotoFormat.Sniff(clean);
            parts.Add(ComputeJobPart.File(
                "photos",
                CaptureComputeDocuments.PhotoName(photo.Index) + CapturePhotoFormat.Extension(kind),
                clean,
                CapturePhotoFormat.ContentType(kind)));
        }

        // The walk-along video's frames (if any): auxiliary images for coverage, never for alignment.
        parts.AddRange(await FramePartsAsync(captureId, ct));
        parts.Add(ComputeJobPart.Json("options", CaptureSplatDocuments.BuildOptions(settings.SplatMaxSteps)));
        return await client.SubmitMultipartAsync(SplatKind, parts, ct);
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

        var row = new WallGeometrySplat
        {
            GeometryModelId = modelId,
            StoredPath = await files.SaveAsync(spz, ".spz", ct),
            SizeBytes = spz.LongLength,
            FrameJson = frameJson,
        };

        await using var db = dbContextFactory.CreateDbContext();
        var old = await db.WallGeometrySplats.Where(s => s.GeometryModelId == modelId).ToListAsync(ct);
        db.WallGeometrySplats.RemoveRange(old);
        db.WallGeometrySplats.Add(row);
        await db.SaveChangesAsync(ct);
        foreach (var splat in old)
        {
            files.Delete(splat.StoredPath);
        }

        logger.LogInformation(
            "Stored the photo-real view of model {ModelId} ({Bytes} bytes, alignment residual {Residual})",
            modelId, row.SizeBytes, CaptureSplatDocuments.ResidualText(frameJson));
    }
}
