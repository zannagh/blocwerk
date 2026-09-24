// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The photo-real stage split between the server and a 3D runner (<see cref="GpuRunner"/>):
/// <list type="number">
/// <item>the splat worker (CPU is enough) runs <c>splat-prepare</c>: photo prep, COLMAP, undistort;</item>
/// <item>its training bundle is sanitised (<see cref="RunnerBundle"/>) and queued as a <see cref="GpuJob"/>;
/// the capture becomes <see cref="WallCaptureStatus.AwaitingRunner"/> and the worker is free;</item>
/// <item>a runner trains and uploads the splat; <see cref="GpuJobQueue"/> sets the capture back to
/// <see cref="WallCaptureStatus.Splatting"/> and re-enqueues it;</item>
/// <item>here again, <c>splat-finish</c> on the splat worker aligns, crops, cleans up and exports,
/// and the result is stored exactly like the all-in-one path's (level-of-detail ladder included).</item>
/// </list>
/// </summary>
public sealed partial class WallCaptureProcessor
{
    internal const string PrepareKind = "splat-prepare";
    internal const string FinishKind = "splat-finish";
    internal const string BundleFile = "bundle.zip";
    internal const string PreparedFile = "prepared.json";
    private const double PrepareEnd = 0.3;
    private const long MaxBundleBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Runs the photo-real stage; false when it was handed to a 3D runner (the capture waits).</summary>
    private async Task<bool> SplatOnServerOrRunnerAsync(WallCapture capture, Guid modelId, IComputeJobClient client, CancellationToken ct)
    {
        if (gpuJobs is null)
        {
            await SplatAllInOneAsync(capture, modelId, client, ct);
            return true;
        }

        var job = await gpuJobs.LatestForCaptureAsync(capture.Id, ct);
        if (job is { Status: GpuJobStatus.Succeeded, InstalledAt: null })
        {
            await FinishRunnerJobAsync(capture, modelId, job, client, ct);
            return true;
        }

        if (job is { Status: GpuJobStatus.Queued or GpuJobStatus.Claimed or GpuJobStatus.Running })
        {
            await AwaitRunnerAsync(capture.Id, ct);
            return false;
        }

        if (!await UseRunnersAsync(capture.WallId, ct))
        {
            await SplatAllInOneAsync(capture, modelId, client, ct);
            return true;
        }

        await PrepareForRunnerAsync(capture, modelId, client, ct);
        return false;
    }

    private async Task<bool> UseRunnersAsync(Guid wallId, CancellationToken ct) => gpuJobs!.Options.Mode switch
    {
        GpuRunnerMode.Off => false,
        GpuRunnerMode.Always => true,
        _ => await gpuJobs.WallHasRunnersAsync(wallId, ct),
    };

    private async Task PrepareForRunnerAsync(WallCapture capture, Guid modelId, IComputeJobClient client, CancellationToken ct)
    {
        if (capture.SplatJobId is null)
        {
            await PrepareVideoFramesAsync(capture.Id, ct);
        }

        var status = await RunJobAsync(
            capture.SplatJobId,
            () => SubmitSplatAsync(capture.Id, modelId, capture.SplatQuality, client, ct, PrepareKind),
            jobId => UpdateAsync(capture.Id, c => c.SplatJobId = jobId, ct),
            client,
            new JobStage(capture.Id, WallCaptureStatus.Splatting, VideoBand, PrepareEnd, "Photo-real view", CaptureSplatDocuments.Describe),
            ct);
        await SetStageAsync(capture.Id, WallCaptureStatus.Splatting, PrepareEnd, "Photo-real view: packing the photos for a 3D runner", ct);
        var zip = await client.DownloadFileAsync(status.JobId!, BundleFile, MaxBundleBytes, ct);
        var prepared = await client.DownloadFileAsync(status.JobId!, PreparedFile, 64L * 1024 * 1024, ct);
        var (bundle, sha) = RunnerBundle.Sanitize(zip);
        var job = new GpuJob
        {
            WallId = capture.WallId,
            CaptureId = capture.Id,
            GeometryModelId = modelId,
            Quality = capture.SplatQuality ?? SplatQuality.High,
            BundlePath = await files.SaveAsync(bundle, ".zip", ct),
            BundleBytes = bundle.LongLength,
            BundleSha256 = sha,
            PreparedPath = await files.SaveAsync(prepared, ".prep", ct),
        };
        await gpuJobs!.EnqueueAsync(job, ct);
        await AwaitRunnerAsync(capture.Id, ct);
    }

    /// <summary>The capture waits for a runner; the lease sweep keeps its stage text current.</summary>
    private Task AwaitRunnerAsync(Guid captureId, CancellationToken ct) => UpdateAsync(captureId, c =>
    {
        c.Status = WallCaptureStatus.AwaitingRunner;
        c.Progress = 0;
        c.Stage = "Photo-real view: waiting for a 3D runner";
    }, ct);

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
        catch (Exception ex) when (ex is CaptureFailedException or ComputeJobException or InvalidDataException or IOException)
        {
            await gpuJobs!.CloseAsync(job.Id, ex.Message, ct);
            throw;
        }

        await gpuJobs!.CloseAsync(job.Id, null, ct);
    }

    private async Task<string> SubmitFinishAsync(GpuJob job, IComputeJobClient client, CancellationToken ct)
    {
        var prepared = await files.ReadAsync(job.PreparedPath, ct)
                       ?? throw new CaptureFailedException("The prepared photo-real data is missing on the server.");
        var splat = await files.ReadAsync(job.ResultPath!, ct)
                    ?? throw new CaptureFailedException("The trained photo-real view is missing on the server.");
        var format = job.ResultFormat == SplatResultFormat.Spz ? SplatResultFormat.Spz : SplatResultFormat.Ply;
        var parts = new List<ComputeJobPart>
        {
            ComputeJobPart.Json("prepared", Encoding.UTF8.GetString(prepared)),
            ComputeJobPart.Json("trainStats", job.ResultStatsJson ?? "{}"),
            ComputeJobPart.File("splat", $"splat.{format}", splat, "application/octet-stream"),
        };
        logger.LogInformation("Finishing GPU job {JobId} on the splat worker ({Bytes} bytes of {Format})", job.Id, splat.LongLength, format);
        return await client.SubmitMultipartAsync(FinishKind, parts, ct);
    }
}
