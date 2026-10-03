// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Storing a finished photo-real view (<c>wall.spz</c> + <c>frame.json</c> from the splat worker) as the model's
/// <see cref="WallGeometrySplat"/>. The files are saved first; the swap of the model's view happens under one lock for the
/// whole process, so a preview (installed beside the capture worker) and a final result never race: the preview's
/// guard runs inside the same lock and refuses once the final result is delivered.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    private static readonly SemaphoreSlim ViewSwapLock = new(1, 1);

    private async Task StoreSplatAsync(Guid captureId, Guid modelId, string jobId, IComputeJobClient client, CancellationToken ct)
    {
        await SetStageAsync(captureId, WallCaptureStatus.Splatting, 0.99, "Photo-real view: saving", ct);
        var row = await BuildSplatRowAsync(modelId, jobId, client, ct);
        await SwapViewAsync(row, guard: null, ct);
    }

    /// <summary>Downloads, checks and saves the finished view's files; the row is not stored yet.</summary>
    private async Task<WallGeometrySplat> BuildSplatRowAsync(Guid modelId, string jobId, IComputeJobClient client, CancellationToken ct)
    {
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
        return new WallGeometrySplat
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
    }

    /// <summary>
    /// Makes <paramref name="row"/> the model's view (the old one and its files go), unless <paramref name="guard"/> (run
    /// under the lock) says no: then the row's files are deleted and false returned.
    /// </summary>
    private async Task<bool> SwapViewAsync(WallGeometrySplat row, Func<Task<bool>>? guard, CancellationToken ct)
    {
        await ViewSwapLock.WaitAsync(ct);
        try
        {
            if (guard is not null && !await guard())
            {
                foreach (var file in SplatLodLadder.Files(row).OfType<string>())
                {
                    files.Delete(file);
                }

                return false;
            }

            await using var db = dbContextFactory.CreateDbContext();
            var old = await db.WallGeometrySplats.Where(s => s.GeometryModelId == row.GeometryModelId).ToListAsync(ct);
            db.WallGeometrySplats.RemoveRange(old);
            db.WallGeometrySplats.Add(row);
            await db.SaveChangesAsync(ct);
            var shared = await Corrections.SharedCaptureFiles.ReferencedAsync(db, ct);
            foreach (var file in old.SelectMany(SplatLodLadder.Files).OfType<string>().Where(f => !shared.Contains(f)))
            {
                files.Delete(file);
            }
        }
        finally
        {
            ViewSwapLock.Release();
        }

        logger.LogInformation(
            "Stored the photo-real view of model {ModelId} ({Bytes} bytes, alignment residual {Residual})",
            row.GeometryModelId, row.SizeBytes, CaptureSplatDocuments.ResidualText(row.FrameJson));
        return true;
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
