// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// The detection half of the post-capture hold proposal search through the recognition queue: <c>detect-holds@2</c> jobs of
/// at most <see cref="PhotosPerJob"/> photos in the background lane, so interactive work goes first and no job holds a
/// runner for long. The boxes become detections as <see cref="RunnerHoldDetection"/>'s do, under the same detector name.
/// Null (the search tries <see cref="RunnerHoldDetection"/>, then the app) in in-process mode, when a runner could not
/// replay the app's detection, when no runner claimed a job within <see cref="GpuRunnerOptions.HoldsClaimWait"/>, a job
/// failed, or the wait passed <see cref="GpuRunnerOptions.HoldsMaxWait"/>. A shutdown leaves the jobs queued: the restart
/// finds them again by caller key.
/// </summary>
public sealed class QueuedCaptureHoldDetection(
    RecognitionQueue queue,
    RunnerHoldDetection models,
    GpuJobQueue gpuJobs,
    ICaptureFileStore files,
    BlocwerkSettings settings,
    ILogger<QueuedCaptureHoldDetection> logger,
    TimeProvider? clock = null)
{
    /// <summary>The most photos one job carries (design §1: short background jobs).</summary>
    public const int PhotosPerJob = 8;

    private static readonly TimeSpan Slice = TimeSpan.FromMinutes(1);
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    /// <summary>The holds per camera name, or null: then the search finds them another way.</summary>
    public async Task<RunnerHoldsFound?> DetectAsync(CaptureFollowUpContext context, CancellationToken ct)
    {
        if (queue.Options.Mode == RecognitionMode.InProcess || RecognitionDetectRequest.For(settings.HoldDetection, models) is not { } request)
        {
            return null;
        }

        var jobs = new List<(RecognitionJob Job, (string Camera, string File)[] Names)>();
        try
        {
            var chunks = (await PhotosAsync(context.ModelId, ct)).Chunk(PhotosPerJob).ToList();
            for (var i = 0; i < chunks.Count; i++)
            {
                jobs.Add(await OpenOrQueueAsync(context, i, chunks[i], request, ct));
            }

            var found = new Dictionary<string, RunnerPhotoHolds>(StringComparer.OrdinalIgnoreCase);
            foreach (var (job, names) in jobs)
            {
                if (await WaitAsync(job, ct) is not { ResultJson: { } json })
                {
                    await CancelAsync(jobs, "another part of the hold search was not served");
                    return null;
                }

                Collect(json, names, found);
            }

            if (jobs.Count == 0)
            {
                return null;
            }

            logger.LogInformation("Capture {CaptureId}: a recognition runner found the holds in {Photos} photos", context.CaptureId, found.Count);
            return new RunnerHoldsFound(models.DetectorName(), found);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CaptureFailedException or DbUpdateException or JsonException)
        {
            logger.LogWarning(ex, "Capture {CaptureId}: the hold search could not go through the recognition queue", context.CaptureId);
            await CancelAsync(jobs, "the hold search stopped");
            return null;
        }
    }

    private static void Collect(string json, (string Camera, string File)[] names, Dictionary<string, RunnerPhotoHolds> found)
    {
        using var document = JsonDocument.Parse(json);
        var photos = RunnerHoldsResult.ParseBoxes(document.RootElement);
        foreach (var (camera, file) in names)
        {
            if (photos.TryGetValue(file, out var p))
            {
                found[camera] = new RunnerPhotoHolds(p.Width, p.Height, RunnerHoldsResult.ToDetections(camera, p.Width, p.Height, p.Boxes));
            }
        }
    }

    /// <summary>The model's photos with a file on disk, as camera name and physical path.</summary>
    private async Task<List<(string Camera, string Path)>> PhotosAsync(Guid modelId, CancellationToken ct) =>
        (await models.PhotosAsync(modelId, ct))
            .Select(p => (p.Name, Path: files.ResolvePhysicalPath(p.StoredPath)))
            .Where(p => p.Path is not null && File.Exists(p.Path))
            .Select(p => (p.Name, p.Path!))
            .ToList();

    private async Task<(RecognitionJob, (string Camera, string File)[])> OpenOrQueueAsync(
        CaptureFollowUpContext context, int index, (string Camera, string Path)[] chunk, object request, CancellationToken ct)
    {
        var key = $"capture-holds@2:{context.ModelId:N}:{index}";
        var names = chunk.Select((p, j) => (p.Camera, File: $"p{j + 1:00}.jpg")).ToArray();
        if (await queue.FindByCallerKeyAsync(key, ct) is { } open && !RecognitionQueue.IsOver(open.Status))
        {
            return (open, names);
        }

        var parts = new List<ComputeJobPart> { RecognitionParts.Request(request) };
        parts.AddRange(chunk.Select((p, j) => ComputeJobPart.FromDisk("photos", names[j].File, p.Path, "image/jpeg")));
        var memory = RecognitionParts.MemoryMb(chunk.Select(p => RecognitionParts.SizeOf(p.Path)), together: false);
        var job = await queue.EnqueueAsync(
            new RecognitionRequest(RecognitionKinds.DetectHolds, RecognitionLane.Background, context.WallId, parts, memory, key), ct);
        return (job, names);
    }

    /// <summary>The job once it ended, or null when it ended unserved or the search stops waiting for it.</summary>
    private async Task<RecognitionJob?> WaitAsync(RecognitionJob job, CancellationToken ct)
    {
        var options = gpuJobs.Options;
        while (true)
        {
            using var slice = CancellationTokenSource.CreateLinkedTokenSource(ct);
            slice.CancelAfter(Slice);
            try
            {
                var ended = await queue.WaitAsync(job.Id, slice.Token);
                return ended is { Status: GpuJobStatus.Succeeded } ? ended : Unserved(job, ended?.Error ?? "the job is gone");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // A background job stays queued; look at it.
            }

            var row = await queue.FindAsync(job.Id, ct);
            var waited = time.GetUtcNow() - job.CreatedAt;
            if (row is null || waited > options.HoldsMaxWait || (row is { Status: GpuJobStatus.Queued, Attempts: 0 } && waited > options.HoldsClaimWait))
            {
                return Unserved(job, $"not served after {waited.TotalMinutes:0} minutes");
            }
        }
    }

    private RecognitionJob? Unserved(RecognitionJob job, string why)
    {
        logger.LogInformation("Recognition job {JobId} (capture hold search) was not served: {Reason}", job.Id, why);
        return null;
    }

    private async Task CancelAsync(List<(RecognitionJob Job, (string Camera, string File)[] Names)> jobs, string reason)
    {
        foreach (var (job, _) in jobs)
        {
            await queue.CancelAsync(job.Id, reason, CancellationToken.None);
        }
    }
}
