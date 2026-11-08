// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;

namespace Blocwerk.Core.Recognition;

/// <summary>
/// Wakes callers waiting for a recognition job when it ends (single-instance app, like <c>GpuJobSignal</c>): one
/// completion source per job, set by the queue's result, fail, cancel and sweep. Only a signal: the caller reads the job
/// from the database, and polls anyway (<see cref="RecognitionQueueOptions.WaiterPoll"/>) in case a signal was missed.
/// </summary>
public sealed class RecognitionWaiters
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> waiting = new();

    /// <summary>How many jobs someone waits for right now.</summary>
    public int Count => waiting.Count;

    /// <summary>A task that completes when <paramref name="jobId"/> ends; register it before reading the job, so no end slips past.</summary>
    public Task Watch(Guid jobId) =>
        waiting.GetOrAdd(jobId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    /// <summary>Wakes whoever waits for <paramref name="jobId"/>.</summary>
    public void Complete(Guid jobId)
    {
        if (waiting.TryRemove(jobId, out var source))
        {
            source.TrySetResult();
        }
    }

    /// <summary>Drops the registration of a job its caller stopped waiting for.</summary>
    public void Forget(Guid jobId) => waiting.TryRemove(jobId, out _);
}
