// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Photo-real previews (<see cref="GpuJobPreviews"/>): a runner's intermediate splat is finished on the splat worker like
/// a final result and installed on the model, so the wall shows a photo-real view long before the training ends. It
/// runs beside the capture worker (<see cref="GpuPreviewWorker"/>), never touches the capture row and never runs the
/// post-capture chain: its photo-real steps would run again on the final view within the hour, and its proposals would
/// come from a half-trained scene. The chain runs once, on the final view (or, when that fails, on a re-finish of the
/// kept preview). A preview that cannot be finished only costs itself.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    /// <summary>Installs the job's pending preview, if it still may be (<see cref="GpuJobPreviews.MayInstall"/>).</summary>
    public async Task InstallPreviewAsync(Guid jobId, CancellationToken ct)
    {
        var job = gpuJobs is null ? null : await gpuJobs.FindAsync(jobId, ct);
        var client = computeClients.Get(ComputeServiceKind.Splat);
        if (job is null || !GpuJobPreviews.IsPending(job) || !client.IsConfigured)
        {
            return;
        }

        var (step, path) = (job.PreviewStep!.Value, job.PreviewPath!);
        try
        {
            var stats = JsonSerializer.Serialize(new { previewStep = step, totalSteps = job.TotalSteps });
            var status = await RunJobAsync(
                job.PreviewFinishJobId,
                () => SubmitFinishAsync(job, path, job.PreviewFormat, stats, client, ct),
                finishJobId => gpuJobs!.SetPreviewFinishJobAsync(job.Id, step, finishJobId, ct),
                client,
                new JobStage(job.CaptureId, WallCaptureStatus.Splatting, 0, 1, GpuJobPreviews.Label(step, job.TotalSteps), Silent: true),
                ct);
            var row = await BuildSplatRowAsync(job.GeometryModelId, status.JobId!, client, ct);
            var installed = await SwapViewAsync(row, () => gpuJobs!.TryMarkPreviewInstalledAsync(job.Id, step, path, ct), ct);
            logger.LogInformation(
                "Preview at step {Step} of GPU job {JobId} {Outcome}", step, job.Id,
                installed ? "installed" : "not installed: a newer view or preview came first, or the job ended");
        }
        catch (Exception ex) when (ex is CaptureFailedException or ComputeJobException or InvalidDataException or IOException)
        {
            logger.LogWarning("Preview at step {Step} of GPU job {JobId} not installed: {Reason}", step, job.Id, ex.Message);
            if (ex is not ComputeJobException c || !IsWorkerAbsent(c))
            {
                await gpuJobs!.DropPreviewAsync(job.Id, step, path, ct);
            }
        }
    }

    /// <summary>
    /// The final view failed for good while a preview is installed: the preview stays the wall's view and the capture
    /// says so (a finished capture keeps its status; one in the photo-real stage ends as done).
    /// </summary>
    internal static void KeepPreview(WallCapture c, string reason, int step, int? total)
    {
        const string polled = "Photo-real view failed: ";
        reason = reason.StartsWith(polled, StringComparison.Ordinal) ? reason[polled.Length..] : reason;
        if (c.Status == WallCaptureStatus.Splatting)
        {
            c.Status = TextureOutcome(c.Error);
            c.Progress = 1;
            c.Stage = "Done (photo-real preview kept)";
            c.CompletedAt = DateTimeOffset.UtcNow;
        }

        c.FollowUpJson = (CaptureFollowUpRecord.Parse(c.FollowUpJson) with { Note = GpuJobPreviews.KeptNote(step, total, reason) }).ToJson();
    }

    /// <summary>The photo-real stage failed: the capture ends without the view, or keeps the installed preview.</summary>
    private async Task EndWithoutViewAsync(Guid captureId, string reason, CancellationToken ct)
    {
        var preview = gpuJobs is null ? null : await gpuJobs.InstalledPreviewAsync(captureId, ct);
        await UpdateAsync(
            captureId,
            c =>
            {
                if (preview is { } p)
                {
                    KeepPreview(c, reason, p.Step, p.Total);
                }
                else
                {
                    EndWithoutSplat(c, reason);
                }
            },
            ct);
    }
}
