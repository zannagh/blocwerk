// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Recognition;

/// <summary>
/// Wakes callers waiting for a recognition job when it ends (single-instance app, like <c>GpuJobSignal</c>): one
/// completion source per job, set by the queue's result, fail, cancel and sweep. Only a signal: the caller reads the job
/// from the database, and polls anyway (<see cref="RecognitionQueueOptions.WaiterPoll"/>) in case a signal was missed.
/// Several callers may wait for one job; each <see cref="Watch"/> is paired with one <see cref="Forget"/>.
/// </summary>
public sealed class RecognitionWaiters
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, (TaskCompletionSource Source, int Count)> waiting = [];

    /// <summary>How many jobs someone waits for right now.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return waiting.Count;
            }
        }
    }

    /// <summary>Registers one waiter for <paramref name="jobId"/>; pair it with <see cref="Forget"/> in a <c>finally</c>.</summary>
    public void Watch(Guid jobId)
    {
        lock (gate)
        {
            var (source, count) = waiting.TryGetValue(jobId, out var entry) ? entry : (NewSource(), 0);
            waiting[jobId] = (source, count + 1);
        }
    }

    /// <summary>
    /// A task that completes at the next <see cref="Complete"/> of a watched job; take it before reading the job, so no end
    /// slips past. Completed at once when nobody watches it.
    /// </summary>
    public Task Next(Guid jobId)
    {
        lock (gate)
        {
            return waiting.TryGetValue(jobId, out var entry) ? entry.Source.Task : Task.CompletedTask;
        }
    }

    /// <summary>Wakes whoever waits for <paramref name="jobId"/>.</summary>
    public void Complete(Guid jobId)
    {
        lock (gate)
        {
            if (waiting.TryGetValue(jobId, out var entry))
            {
                entry.Source.TrySetResult();
                waiting[jobId] = (NewSource(), entry.Count);
            }
        }
    }

    /// <summary>One waiter of <paramref name="jobId"/> stopped waiting; the last one drops the entry.</summary>
    public void Forget(Guid jobId)
    {
        lock (gate)
        {
            if (!waiting.TryGetValue(jobId, out var entry))
            {
                return;
            }

            if (entry.Count <= 1)
            {
                waiting.Remove(jobId);
            }
            else
            {
                waiting[jobId] = (entry.Source, entry.Count - 1);
            }
        }
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
