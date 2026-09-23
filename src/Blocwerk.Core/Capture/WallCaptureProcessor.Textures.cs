using System.Text.Json;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Stage 3: per-facet textures for the 3D view. The model is already active by now, so a texture
/// failure does not undo it: the capture ends as <see cref="WallCaptureStatus.SucceededWithoutTextures"/>
/// (after the optional photo-real stage, see <c>WallCaptureProcessor.Splat.cs</c>).
/// </summary>
public sealed partial class WallCaptureProcessor
{
    private const string TexturesKind = "textures";

    /// <summary>Largest texture file accepted from the worker.</summary>
    private const long MaxTextureBytes = 64L * 1024 * 1024;

    /// <summary>Runs the texture job. Returns null on success, else the (admin-safe) reason textures are missing.</summary>
    private async Task<string?> TextureAsync(CaptureRun run, IComputeJobClient client, CancellationToken ct)
    {
        var capture = run.Capture;
        var modelId = capture.GeometryModelId!.Value;
        try
        {
            var status = await RunJobAsync(
                capture.TexturesJobId,
                () => SubmitTexturesAsync(capture.Id, modelId, client, ct),
                jobId => UpdateAsync(capture.Id, c => c.TexturesJobId = jobId, ct),
                client,
                new JobStage(capture.Id, WallCaptureStatus.Texturing, 0.75, 0.95, "Rendering wall textures"),
                ct);
            await StoreTexturesAsync(capture.Id, modelId, status, client, ct);
            return null;
        }
        catch (Exception ex) when (ex is CaptureFailedException or ComputeJobException or InvalidDataException
                                       or IOException or DbUpdateException)
        {
            logger.LogWarning(ex, "Textures of capture {CaptureId} failed; the model stays active", capture.Id);
            var reason = ex is DbUpdateException ? "they could not be saved." : ex.Message;
            return $"The 3D model is active, but its textures could not be made: {reason}";
        }
    }

    private async Task<string> SubmitTexturesAsync(Guid captureId, Guid modelId, IComputeJobClient client, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var stored = await db.WallGeometryModels.Where(m => m.Id == modelId).Select(m => m.Json).FirstAsync(ct);

        // Facets carried over from the previous model were not photographed now: they keep its textures.
        var geometry = RegisteredGeometry.WithoutFacets(stored, RegisteredGeometry.Carried(stored).CarriedFacets);
        var cameras = CameraNames(geometry);
        var parts = new List<ComputeJobPart> { ComputeJobPart.Json("geometry", geometry) };
        foreach (var photo in await LoadPhotosAsync(captureId, ct))
        {
            var name = CaptureComputeDocuments.PhotoName(photo.Index);
            if (!cameras.Contains(name))
            {
                continue;
            }

            var bytes = await files.ReadAsync(photo.StoredPath, ct)
                        ?? throw new CaptureFailedException($"Photo {photo.Index} is missing on the server.");

            // Nothing leaves this server with metadata: no GPS, no camera serials, no orientation.
            var clean = ImageMetadataStripper.Strip(bytes);
            var kind = CapturePhotoFormat.Sniff(clean);
            parts.Add(ComputeJobPart.File(
                "photos", name + CapturePhotoFormat.Extension(kind), clean, CapturePhotoFormat.ContentType(kind)));
        }

        if (parts.Count == 1)
        {
            throw new CaptureFailedException("None of the photos was used by the 3D model.");
        }

        return await client.SubmitMultipartAsync(TexturesKind, parts, ct);
    }

    private async Task StoreTexturesAsync(
        Guid captureId, Guid modelId, ComputeJobStatus status, IComputeJobClient client, CancellationToken ct)
    {
        var manifest = CaptureComputeDocuments.ParseTextureResult(status.Result);
        if (manifest.Count == 0)
        {
            throw new CaptureFailedException("the service returned no textures.");
        }

        await SetStageAsync(captureId, WallCaptureStatus.Texturing, 0.96, "Saving wall textures", ct);
        var rows = new List<WallGeometryTexture>();
        try
        {
            foreach (var entry in manifest)
            {
                rows.Add(await DownloadTextureAsync(modelId, entry, status.JobId!, client, ct));
            }

            await CopyCarriedTexturesAsync(modelId, rows, ct);

            await ReplaceTexturesAsync(modelId, rows, ct);
        }
        catch
        {
            // Nothing references the files of a set that was not committed.
            DeleteTextureFiles(rows);

            throw;
        }
    }

    private async Task<WallGeometryTexture> DownloadTextureAsync(
        Guid modelId, TextureManifestEntry entry, string jobId, IComputeJobClient client, CancellationToken ct)
    {
        var bytes = await client.DownloadFileAsync(jobId, entry.File, MaxTextureBytes, ct);
        var kind = CapturePhotoFormat.Sniff(bytes);
        if (kind is not (CapturePhotoKind.Jpeg or CapturePhotoKind.Png))
        {
            throw new CaptureFailedException($"texture {entry.FacetId} is not an image.");
        }

        var mask = await DownloadMaskAsync(entry, jobId, client, ct);
        return new WallGeometryTexture
        {
            GeometryModelId = modelId,
            FacetId = entry.FacetId,
            StoredPath = await files.SaveAsync(bytes, CapturePhotoFormat.Extension(kind), ct),
            ContentType = CapturePhotoFormat.ContentType(kind),
            SizeBytes = bytes.LongLength,
            MaskStoredPath = mask is null ? null : await files.SaveAsync(mask, ".png", ct),
            MaskSizeBytes = mask?.LongLength,
            AMin = entry.AMin,
            AMax = entry.AMax,
            BMin = entry.BMin,
            BMax = entry.BMax,
            WidthPx = entry.WidthPx,
            HeightPx = entry.HeightPx,
        };
    }

    /// <summary>
    /// The facet's coverage mask, or null: a worker without masks, or a mask that is not a PNG or does not
    /// download. The mask is cosmetic (uncovered parts show the plain facet instead of black), so its
    /// absence never fails the textures; the texture then renders opaque as before.
    /// </summary>
    private async Task<byte[]?> DownloadMaskAsync(
        TextureManifestEntry entry, string jobId, IComputeJobClient client, CancellationToken ct)
    {
        if (entry.MaskFile is null)
        {
            return null;
        }

        try
        {
            var bytes = await client.DownloadFileAsync(jobId, entry.MaskFile, MaxTextureBytes, ct);
            if (CapturePhotoFormat.Sniff(bytes) == CapturePhotoKind.Png)
            {
                return bytes;
            }

            logger.LogWarning("The coverage mask of texture {FacetId} is not a PNG; it is left out", entry.FacetId);
        }
        catch (Exception ex) when (ex is ComputeJobException or InvalidDataException or IOException)
        {
            logger.LogWarning(ex, "The coverage mask of texture {FacetId} could not be downloaded", entry.FacetId);
        }

        return null;
    }

    /// <summary>Swaps the model's texture set in one SaveChanges; the old files are removed afterwards.</summary>
    private async Task ReplaceTexturesAsync(Guid modelId, List<WallGeometryTexture> rows, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var old = await db.WallGeometryTextures.Where(t => t.GeometryModelId == modelId).ToListAsync(ct);
        db.WallGeometryTextures.RemoveRange(old);
        db.WallGeometryTextures.AddRange(rows);
        await db.SaveChangesAsync(ct);
        DeleteTextureFiles(old);
    }

    private void DeleteTextureFiles(IEnumerable<WallGeometryTexture> textures)
    {
        foreach (var texture in textures)
        {
            files.Delete(texture.StoredPath);
            if (texture.MaskStoredPath is { } mask)
            {
                files.Delete(mask);
            }
        }
    }

    private Task CompleteAsync(Guid captureId, WallCaptureStatus status, string? error, CancellationToken ct) =>
        UpdateAsync(captureId, c =>
        {
            c.Status = status;
            c.Progress = 1;
            c.Stage = status switch
            {
                WallCaptureStatus.Succeeded => "Done",
                WallCaptureStatus.SucceededWithoutSplat => "Done (without the photo-real view)",
                _ => "Done (without textures)",
            };
            c.Error = error is { Length: > 2048 } ? error[..2048] : error;
            c.CompletedAt = DateTimeOffset.UtcNow;
        }, ct);

    private static HashSet<string> CameraNames(string geometryJson)
    {
        using var doc = JsonDocument.Parse(geometryJson);
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("cameras", out var cameras) && cameras.ValueKind == JsonValueKind.Array)
        {
            foreach (var camera in cameras.EnumerateArray())
            {
                if (camera.TryGetProperty("image", out var image) && image.GetString() is { } name)
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }
}
