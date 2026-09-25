// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The photo-real stage split between the server and a 3D runner (<see cref="GpuRunner"/>):
/// <list type="number">
/// <item>the splat worker (CPU is enough) runs <c>splat-prepare</c>: photo prep, COLMAP, undistort;</item>
/// <item>its training bundle is streamed to disk, sanitised (<see cref="RunnerBundle"/>) and queued as a
/// <see cref="GpuJob"/>; the capture then COMPLETES as "Model ready" (the photo-real view shows as pending) and the
/// worker is free: nobody waits for a runner that may never come;</item>
/// <item>a runner trains and uploads the splat; <see cref="GpuJobQueue"/> puts the capture back into
/// <see cref="WallCaptureStatus.Splatting"/> (like a retrain) and re-enqueues it;</item>
/// <item>here again, <c>splat-finish</c> on the splat worker aligns, crops, cleans up and exports, the result is stored
/// exactly like the all-in-one path's, and the post-capture chain runs its photo-real steps for the new view.</item>
/// </list>
/// </summary>
/// <remarks>
/// Route (<see cref="GpuRunnerMode"/>): <c>off</c> never uses runners; <c>auto</c> uses one only when a runner that may
/// train this wall at this quality is online right now, else the splat worker trains as before; <c>always</c> queues
/// for a runner whatever is online. Both need a splat worker that offers the split kinds (its <c>/health</c>); one that
/// doesn't trains all-in-one as before. A restart during <c>splat-prepare</c> resumes the same kind: the job id is
/// stored with <see cref="PrepareMark"/>.
/// </remarks>
public sealed partial class WallCaptureProcessor
{
    internal const string PrepareKind = "splat-prepare";
    internal const string FinishKind = "splat-finish";
    internal const string BundleFile = "bundle.zip";
    internal const string PreparedFile = "prepared.json";

    /// <summary>Prefix of <see cref="WallCapture.SplatJobId"/> when that job is a <c>splat-prepare</c>.</summary>
    internal const string PrepareMark = "prepare:";

    private const double PrepareEnd = 0.9;
    private const long MaxPreparedBytes = 64L * 1024 * 1024;

    /// <summary>What the photo-real stage did.</summary>
    private enum SplatOutcome
    {
        /// <summary>A new photo-real view is stored.</summary>
        Stored,

        /// <summary>Queued for a 3D runner: the capture completes now, the view follows when a runner delivers it.</summary>
        AwaitingRunner,
    }

    private async Task<SplatOutcome> SplatOnServerOrRunnerAsync(WallCapture capture, Guid modelId, IComputeJobClient client, CancellationToken ct)
    {
        if (gpuJobs is null || gpuJobs.Options.Mode == GpuRunnerMode.Off)
        {
            await SplatAllInOneAsync(capture, modelId, client, ct);
            return SplatOutcome.Stored;
        }

        var job = await gpuJobs.LatestForCaptureAsync(capture.Id, ct);
        if (job is { Status: GpuJobStatus.Succeeded, InstalledAt: null })
        {
            await FinishRunnerJobAsync(capture, modelId, job, client, ct);
            return SplatOutcome.Stored;
        }

        if (job is { Status: GpuJobStatus.Queued or GpuJobStatus.Claimed or GpuJobStatus.Running })
        {
            return SplatOutcome.AwaitingRunner;
        }

        if (capture.SplatJobId?.StartsWith(PrepareMark, StringComparison.Ordinal) == true
            || (capture.SplatJobId is null && await UseRunnerAsync(capture, client, ct)))
        {
            await PrepareForRunnerAsync(capture, modelId, client, ct);
            return SplatOutcome.AwaitingRunner;
        }

        await SplatAllInOneAsync(capture, modelId, client, ct);
        return SplatOutcome.Stored;
    }

    private async Task<bool> UseRunnerAsync(WallCapture capture, IComputeJobClient client, CancellationToken ct)
    {
        var quality = capture.SplatQuality ?? SplatQuality.High;

        // Ultra waits for an ultra runner even when none is online: the splat worker would only train it as Max.
        var wanted = gpuJobs!.Options.Mode == GpuRunnerMode.Always
                     || await gpuJobs.HasEligibleRunnerOnlineAsync(capture.WallId, quality, ct)
                     || (quality == SplatQuality.Ultra && await gpuJobs.UltraAvailableForWallAsync(capture.WallId, ct));
        if (!wanted)
        {
            return false;
        }

        try
        {
            var kinds = (await client.GetHealthAsync(ct)).Kinds;
            if (kinds.Contains(PrepareKind) && kinds.Contains(FinishKind))
            {
                return true;
            }

            logger.LogInformation("Capture {CaptureId}: the splat worker has no {Kind}; it trains the photo-real view itself", capture.Id, PrepareKind);
        }
        catch (ComputeJobException ex)
        {
            logger.LogInformation("Capture {CaptureId}: could not ask the splat worker for its kinds ({Reason})", capture.Id, ex.Message);
        }

        return false;
    }

    private async Task PrepareForRunnerAsync(WallCapture capture, Guid modelId, IComputeJobClient client, CancellationToken ct)
    {
        var resumed = capture.SplatJobId?[PrepareMark.Length..];
        if (resumed is null)
        {
            await PrepareVideoFramesAsync(capture.Id, ct);
        }

        var status = await RunJobAsync(
            resumed,
            () => SubmitSplatAsync(capture.Id, modelId, capture.SplatQuality, client, ct, PrepareKind),
            jobId => UpdateAsync(capture.Id, c => c.SplatJobId = PrepareMark + jobId, ct),
            client,
            new JobStage(capture.Id, WallCaptureStatus.Splatting, VideoBand, PrepareEnd, "Photo-real view", CaptureSplatDocuments.Describe),
            ct);
        await SetStageAsync(capture.Id, WallCaptureStatus.Splatting, PrepareEnd, "Photo-real view: packing the photos for a 3D runner", ct);
        var (bundle, bytes, sha) = await StoreBundleAsync(status.JobId!, client, ct);
        var prepared = await client.DownloadFileAsync(status.JobId!, PreparedFile, MaxPreparedBytes, ct);
        await gpuJobs!.EnqueueAsync(
            new GpuJob
            {
                WallId = capture.WallId,
                CaptureId = capture.Id,
                GeometryModelId = modelId,
                Quality = capture.SplatQuality ?? SplatQuality.High,
                BundlePath = bundle,
                BundleBytes = bytes,
                BundleSha256 = sha,
                PreparedPath = await files.SaveAsync(prepared, ".prep", ct),
            },
            ct);
    }

    /// <summary>The worker's bundle, streamed to disk under the size cap, then rebuilt (sanitised) into the store.</summary>
    private async Task<(string Stored, long Bytes, string Sha256)> StoreBundleAsync(string jobId, IComputeJobClient client, CancellationToken ct)
    {
        string? raw = null;
        try
        {
            raw = await client.ReadFileAsync(
                jobId, BundleFile, (body, token) => files.SaveStreamAsync(body, ".zip", gpuJobs!.Options.MaxBundleBytes, token), ct);
            var rawPath = files.ResolvePhysicalPath(raw) ?? throw new IOException("The training bundle was not stored.");
            var stored = await files.SaveWithAsync(
                ".zip",
                async (output, _) =>
                {
                    await using var input = File.OpenRead(rawPath);
                    RunnerBundle.Sanitize(input, output);
                },
                ct);
            var path = files.ResolvePhysicalPath(stored) ?? throw new IOException("The training bundle was not stored.");
            return (stored, new FileInfo(path).Length, await RunnerBundle.Sha256Async(path, ct));
        }
        catch (CaptureFileTooLargeException ex)
        {
            throw new InvalidDataException($"the training bundle is too large ({ex.Message})");
        }
        finally
        {
            files.Delete(raw);
        }
    }

    private async Task FinishRunnerJobAsync(WallCapture capture, Guid modelId, GpuJob job, IComputeJobClient client, CancellationToken ct)
    {
        try
        {
            var status = await RunJobAsync(
                job.FinishJobId,
                () => SubmitFinishAsync(job, client, ct),
                jobId => gpuJobs!.SetFinishJobAsync(job.Id, jobId, ct),
                client,
                new JobStage(capture.Id, WallCaptureStatus.Splatting, 0.96, 0.98, "Photo-real view", CaptureSplatDocuments.Describe),
                ct);
            await StoreSplatAsync(capture.Id, modelId, status.JobId!, client, ct);
        }
        catch (Exception ex) when (ex is CaptureFailedException or InvalidDataException or IOException
                                   || (ex is ComputeJobException c && !IsWorkerAbsent(c)))
        {
            // An unreachable worker keeps the delivered result: the sweep hands it back later (GpuJobQueue.SweepAsync).
            await gpuJobs!.CloseAsync(job.Id, ex.Message, ct);
            throw;
        }

        await gpuJobs!.CloseAsync(job.Id, null, ct);
    }

    private async Task<string> SubmitFinishAsync(GpuJob job, IComputeJobClient client, CancellationToken ct)
    {
        var prepared = await files.ReadAsync(job.PreparedPath, ct)
                       ?? throw new CaptureFailedException("The prepared photo-real data is missing on the server.");
        var splat = job.ResultPath is null ? null : files.ResolvePhysicalPath(job.ResultPath);
        if (splat is null || !File.Exists(splat))
        {
            throw new CaptureFailedException("The trained photo-real view is missing on the server.");
        }

        var format = job.ResultFormat == SplatResultFormat.Spz ? SplatResultFormat.Spz : SplatResultFormat.Ply;
        var parts = new List<ComputeJobPart>
        {
            ComputeJobPart.Json("prepared", System.Text.Encoding.UTF8.GetString(prepared)),
            ComputeJobPart.Json("trainStats", job.ResultStatsJson ?? "{}"),
            ComputeJobPart.FromDisk("splat", $"splat.{format}", splat, "application/octet-stream"),
        };
        logger.LogInformation("Finishing GPU job {JobId} on the splat worker ({Bytes} bytes of {Format})", job.Id, job.ResultBytes, format);
        return await client.SubmitMultipartAsync(FinishKind, parts, ct);
    }
}
