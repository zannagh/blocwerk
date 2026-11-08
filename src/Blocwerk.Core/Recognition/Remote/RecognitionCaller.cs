// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// Runs one interactive recognition job through the <see cref="RecognitionQueue"/> for the remote clients and decides by
/// <see cref="RecognitionQueueOptions.Mode"/> what a job no runner served means (nobody claimed it within the interactive
/// deadline, it failed, it could not be queued, its result is unreadable): <c>queue+fallback</c> hands it back to the app,
/// <c>queue</c> throws <see cref="ImageRecognitionUnavailableException"/> ("recognition unavailable, retry"). In
/// <c>queue+fallback</c> a job is not even queued while the recognition pod is gone
/// (<see cref="RecognitionQueueOptions.InstanceStaleAfter"/>). A background job has no claim deadline: in <c>queue</c> it waits
/// until a runner serves it, in <c>queue+fallback</c> until the pod is gone while it is still unclaimed.
/// </summary>
public sealed class RecognitionCaller(RecognitionQueue queue, ILogger<RecognitionCaller> logger)
{
    private readonly ConcurrentDictionary<string, bool> unscopedKinds = new(StringComparer.Ordinal);
    private int podGoneLogged;

    /// <summary>Gets a value indicating whether recognition goes through the queue at all.</summary>
    public bool Queued => queue.Options.Mode != RecognitionMode.InProcess;

    /// <summary>Gets how long one wait for a background job lasts before the caller looks at the pod again.</summary>
    internal TimeSpan BackgroundSlice { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The kind's result, read by <paramref name="read"/>; null when the caller runs it in the app instead: in-process
    /// mode, no wall in scope (<see cref="RecognitionWallScope"/>), or the fallback.
    /// </summary>
    /// <typeparam name="T">The in-process result type.</typeparam>
    public Task<T?> RunAsync<T>(
        string kind, IReadOnlyList<ComputeJobPart> parts, int? memoryMb, Func<JsonElement, T> read, CancellationToken ct)
        where T : class =>
        RunAsync(kind, RecognitionLane.Interactive, null, parts, memoryMb, read, ct);

    /// <summary>
    /// Like <see cref="RunAsync{T}(string, IReadOnlyList{ComputeJobPart}, int?, Func{JsonElement, T}, CancellationToken)"/> in
    /// <paramref name="lane"/>. A job queued under <paramref name="callerKey"/> before (a restarted background run) is taken up
    /// again unless it failed or was cancelled.
    /// </summary>
    /// <typeparam name="T">The in-process result type.</typeparam>
    public async Task<T?> RunAsync<T>(
        string kind,
        RecognitionLane lane,
        string? callerKey,
        IReadOnlyList<ComputeJobPart> parts,
        int? memoryMb,
        Func<JsonElement, T> read,
        CancellationToken ct)
        where T : class
    {
        if (!Queued)
        {
            return null;
        }

        if (RecognitionWallScope.Current is not { } wallId)
        {
            if (unscopedKinds.TryAdd(kind, true))
            {
                logger.LogWarning("Recognition job {Kind} has no wall in scope; it runs in the app (logged once per kind)", kind);
            }

            return null;
        }

        if (!await PodAliveOrNoFallbackAsync(kind, ct))
        {
            return null;
        }

        var (json, why) = await TryRunAsync(new RecognitionRequest(kind, lane, wallId, parts, memoryMb, callerKey), ct);
        if (json is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                return read(document.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException
                                           or IndexOutOfRangeException or FormatException or ArgumentException)
            {
                why = $"its result cannot be read ({ex.Message})";
            }
        }

        return Unserved<T>(kind, wallId, why);
    }

    /// <summary>False in <c>queue+fallback</c> while the pod is gone (logged once until it is back): the app runs the job.</summary>
    private async Task<bool> PodAliveOrNoFallbackAsync(string kind, CancellationToken ct)
    {
        if (queue.Options.Mode != RecognitionMode.QueueWithFallback || await queue.InstanceRunnerAliveAsync(ct))
        {
            Interlocked.Exchange(ref podGoneLogged, 0);
            return true;
        }

        if (Interlocked.Exchange(ref podGoneLogged, 1) == 0)
        {
            logger.LogWarning(
                "The recognition pod has not been seen for {Seconds} s; recognition ({Kind} and later jobs) runs in the app until it is back",
                queue.Options.InstanceStaleAfter.TotalSeconds, kind);
        }

        return false;
    }

    private async Task<(string? ResultJson, string Why)> TryRunAsync(RecognitionRequest request, CancellationToken ct)
    {
        var queued = request.CallerKey is { } key ? await queue.FindByCallerKeyAsync(key, ct) : null;
        if (queued is null or { Status: GpuJobStatus.Failed or GpuJobStatus.Cancelled })
        {
            try
            {
                queued = await queue.EnqueueAsync(request, ct);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or CaptureFailedException or DbUpdateException)
            {
                return (null, $"it could not be queued ({ex.Message})");
            }
        }

        var job = RecognitionQueue.IsOver(queued.Status) ? queued
            : request.Lane == RecognitionLane.Interactive ? await queue.WaitAsync(queued.Id, ct)
            : await WaitInBackgroundAsync(queued.Id, ct);
        return job switch
        {
            null => (null, "the job is gone"),
            { Status: GpuJobStatus.Succeeded, ResultJson: { } json } => (json, string.Empty),
            _ => (null, job.Error ?? $"it ended {job.Status} without a result"),
        };
    }

    /// <summary>
    /// Waits for a background job in slices. In <c>queue+fallback</c> a job still unclaimed while the pod is gone is cancelled,
    /// so the app runs it; in <c>queue</c> it stays queued until a runner serves it.
    /// </summary>
    private async Task<RecognitionJob?> WaitInBackgroundAsync(Guid jobId, CancellationToken ct)
    {
        while (true)
        {
            using var slice = CancellationTokenSource.CreateLinkedTokenSource(ct);
            slice.CancelAfter(BackgroundSlice);
            try
            {
                return await queue.WaitAsync(jobId, slice.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // A background job stays queued when its waiter stops; look at the pod.
            }

            if (queue.Options.Mode == RecognitionMode.QueueWithFallback
                && await queue.FindAsync(jobId, ct) is { Status: GpuJobStatus.Queued }
                && !await queue.InstanceRunnerAliveAsync(ct))
            {
                await queue.CancelAsync(jobId, "the recognition pod is gone", CancellationToken.None);
                return await queue.FindAsync(jobId, CancellationToken.None);
            }
        }
    }

    private T? Unserved<T>(string kind, Guid wallId, string why)
        where T : class
    {
        if (queue.Options.Mode == RecognitionMode.QueueWithFallback)
        {
            logger.LogWarning("Recognition job {Kind} for wall {WallId} was not served: {Reason}; running it in the app instead", kind, wallId, why);
            return null;
        }

        logger.LogWarning("Recognition job {Kind} for wall {WallId} was not served: {Reason}; recognition unavailable", kind, wallId, why);
        throw new ImageRecognitionUnavailableException(ImageRecognitionUnavailableException.QueueMessage);
    }
}
