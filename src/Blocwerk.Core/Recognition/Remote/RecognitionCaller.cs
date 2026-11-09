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
/// (<see cref="RecognitionQueueOptions.InstanceStaleAfter"/>). A background job unclaimed for
/// <see cref="RecognitionQueueOptions.BackgroundClaimWait"/> is cancelled (and in <c>queue+fallback</c> also once the pod is gone):
/// <c>queue+fallback</c> runs it in the app, <c>queue</c> throws <see cref="ImageRecognitionUnavailableException.WorkerNotRunningMessage"/>.
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
    /// again while it has not ended; an ended one never is, so a result that could not be read is not read again.
    /// </summary>
    /// <typeparam name="T">The in-process result type.</typeparam>
    public async Task<T?> RunAsync<T>(
        string kind,
        RecognitionLane lane,
        string? callerKey,
        IReadOnlyList<ComputeJobPart> parts,
        int? memoryMb,
        Func<JsonElement, T> read,
        CancellationToken ct,
        int seeds = 0)
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

        var (json, why) = await TryRunAsync(new RecognitionRequest(kind, lane, wallId, parts, memoryMb, callerKey, seeds), ct);
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
        if (queued is null || RecognitionQueue.IsOver(queued.Status))
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

        var job = request.Lane == RecognitionLane.Interactive ? await queue.WaitAsync(queued.Id, ct) : await WaitInBackgroundAsync(queued.Id, ct);
        return job switch
        {
            null => (null, "the job is gone"),
            { Status: GpuJobStatus.Succeeded, ResultJson: { } json } => (json, string.Empty),
            _ => (null, job.Error ?? $"it ended {job.Status} without a result"),
        };
    }

    /// <summary>Waits for a background job in slices, until it ends or <see cref="GiveUpAsync"/> cancels it.</summary>
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

            if (await queue.FindAsync(jobId, ct) is { Status: GpuJobStatus.Queued } row && await GiveUpAsync(row, ct))
            {
                return await queue.FindAsync(jobId, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Cancels a still queued background job nobody claimed for <see cref="RecognitionQueueOptions.BackgroundClaimWait"/>, or in
    /// <c>queue+fallback</c> whose pod is gone. In <c>queue</c> that throws: the recognition worker is not running.
    /// </summary>
    private async Task<bool> GiveUpAsync(RecognitionJob row, CancellationToken ct)
    {
        var fallback = queue.Options.Mode == RecognitionMode.QueueWithFallback;
        var unclaimed = row.Attempts == 0 && queue.Now - row.CreatedAt > queue.Options.BackgroundClaimWait;
        var podGone = fallback && !await queue.InstanceRunnerAliveAsync(ct);
        if (!unclaimed && !podGone)
        {
            return false;
        }

        var why = podGone ? "the recognition pod is gone" : $"no recognition runner took it within {queue.Options.BackgroundClaimWait.TotalMinutes:0} minutes";
        logger.LogWarning("Background recognition job {JobId} ({Kind}) for wall {WallId} is cancelled: {Reason}", row.Id, row.Kind, row.WallId, why);
        await queue.CancelAsync(row.Id, why, CancellationToken.None);
        return fallback ? true : throw new ImageRecognitionUnavailableException(ImageRecognitionUnavailableException.WorkerNotRunningMessage);
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
