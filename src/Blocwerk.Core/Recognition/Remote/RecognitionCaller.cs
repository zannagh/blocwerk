// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

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
/// <c>queue</c> throws <see cref="ImageRecognitionUnavailableException"/> ("recognition unavailable, retry").
/// </summary>
public sealed class RecognitionCaller(RecognitionQueue queue, ILogger<RecognitionCaller> logger)
{
    /// <summary>Gets a value indicating whether recognition goes through the queue at all.</summary>
    public bool Queued => queue.Options.Mode != RecognitionMode.InProcess;

    /// <summary>
    /// The kind's result, read by <paramref name="read"/>; null when the caller runs it in the app instead: in-process
    /// mode, no wall in scope (<see cref="RecognitionWallScope"/>), or the fallback.
    /// </summary>
    public async Task<T?> RunAsync<T>(
        string kind, IReadOnlyList<ComputeJobPart> parts, int? memoryMb, Func<JsonElement, T> read, CancellationToken ct)
        where T : class
    {
        if (!Queued || RecognitionWallScope.Current is not { } wallId)
        {
            return null;
        }

        var (json, why) = await TryRunAsync(new RecognitionRequest(kind, RecognitionLane.Interactive, wallId, parts, memoryMb), ct);
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

    private async Task<(string? ResultJson, string Why)> TryRunAsync(RecognitionRequest request, CancellationToken ct)
    {
        RecognitionJob queued;
        try
        {
            queued = await queue.EnqueueAsync(request, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CaptureFailedException or DbUpdateException)
        {
            return (null, $"it could not be queued ({ex.Message})");
        }

        var job = await queue.WaitAsync(queued.Id, ct);
        return job switch
        {
            null => (null, "the job is gone"),
            { Status: GpuJobStatus.Succeeded, ResultJson: { } json } => (json, string.Empty),
            _ => (null, job.Error ?? $"it ended {job.Status} without a result"),
        };
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
