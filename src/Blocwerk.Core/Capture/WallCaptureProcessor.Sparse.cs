// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Sparse;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The capture's sparse points (<see cref="WallCapture.SparsePointsStoredPath"/>): whenever a <c>splat-prepare</c> ran for it
/// (the markerless reconstruction, or the CPU half of a runner-trained photo-real view), its <c>sparse.zip</c> is reduced to
/// points + photo centres (<see cref="ColmapSparseReader"/>) and kept, so volumes and hold protrusion can be measured
/// without a photo-real view. Best effort: a missing or unreadable model only means those steps wait for a splat.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    /// <summary>Downloads the prepare job's sparse.zip and keeps its points, unless the capture already has them.</summary>
    private async Task KeepSparsePointsAsync(WallCapture capture, string prepareJobId, IComputeJobClient client, CancellationToken ct)
    {
        if (capture.SparsePointsStoredPath is not null)
        {
            return;
        }

        byte[] sparse;
        try
        {
            sparse = await client.DownloadFileAsync(prepareJobId, SparseFile, MaxSparseBytes, ct);
        }
        catch (ComputeJobException ex)
        {
            logger.LogInformation("Capture {CaptureId}: no sparse model from job {JobId} ({Reason})", capture.Id, prepareJobId, ex.Message);
            return;
        }

        await KeepSparsePointsAsync(capture, sparse, ct);
    }

    /// <summary>Keeps the points of a downloaded sparse.zip (replacing any the capture had).</summary>
    private async Task KeepSparsePointsAsync(WallCapture capture, byte[] sparseZip, CancellationToken ct)
    {
        SparseCloud cloud;
        try
        {
            cloud = await Task.Run(() => ColmapSparseReader.Read(sparseZip), ct);
        }
        catch (InvalidDataException ex)
        {
            logger.LogInformation("Capture {CaptureId}: the sparse model is not usable for measuring ({Reason})", capture.Id, ex.Message);
            return;
        }

        if (cloud.Count == 0 || cloud.PhotoCentres.Count < SparseWorldAlignment.MinPhotos)
        {
            logger.LogInformation(
                "Capture {CaptureId}: the sparse model has {Points} points and {Photos} photos; not kept", capture.Id, cloud.Count, cloud.PhotoCentres.Count);
            return;
        }

        var stored = await files.SaveAsync(SparseCloudFile.Write(cloud), SparseCloudFile.Extension, ct);
        var old = capture.SparsePointsStoredPath;
        capture.SparsePointsStoredPath = stored;
        await UpdateAsync(capture.Id, c => c.SparsePointsStoredPath = stored, ct);
        files.Delete(old);
        logger.LogInformation(
            "Capture {CaptureId}: kept {Points} sparse points over {Photos} photos for measuring without a photo-real view",
            capture.Id, cloud.Count, cloud.PhotoCentres.Count);
    }
}
