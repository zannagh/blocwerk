// <copyright file="WallEnhancementRunner.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// Hosts the background enhancement runs, one per run at most. A singleton so a run outlives the circuit that started
/// it; every check happened in <see cref="WallEnhancementService"/> before <see cref="Launch"/>. In-process only (the app
/// is single-instance): a restart stops the live run, and <see cref="WallEnhancementResumer"/> starts it again. A run
/// can be cancelled (<see cref="Cancel"/>): the job stops at the next await, records the step as failed and opens the
/// review, where the step can be retried or the run discarded.
/// </summary>
public sealed class WallEnhancementRunner(WallEnhancementJob job) : IDisposable
{
    /// <summary>Runs that may work at once, server-wide (each traces photos with OpenCV); further runs queue.</summary>
    public const int MaxConcurrentRuns = 1;

    /// <summary>
    /// The runs live in this process, whatever their status says: a cancelled or timed-out step can still be working after
    /// its run left Running. Static because the wall-update guard (<see cref="WallEnhancementGuard"/>) reads it from
    /// services that have no runner (the app is single-instance).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> Live = new();

    private readonly SemaphoreSlim slots = new(MaxConcurrentRuns, MaxConcurrentRuns);
    private readonly CancellationTokenSource shutdown = new();
    private readonly object gate = new();
    private readonly Dictionary<Guid, (Task Task, CancellationTokenSource Cancel)> runs = [];

    /// <summary>Whether the run's job is still live in this process (any runner).</summary>
    /// <param name="runId">The run.</param>
    /// <returns>True while its job has not returned.</returns>
    public static bool IsLive(Guid runId) => Live.ContainsKey(runId);

    /// <summary>The job the runner runs (its steps, for the progress line).</summary>
    public WallEnhancementJob Job => job;

    /// <summary>Whether the run is live in this process.</summary>
    /// <param name="runId">The run.</param>
    /// <returns>True while it runs or waits for its slot.</returns>
    public bool IsRunning(Guid runId)
    {
        lock (gate)
        {
            return runs.ContainsKey(runId);
        }
    }

    /// <summary>Starts the run unless it is already live. The run must already be marked Running.</summary>
    /// <param name="runId">The run.</param>
    public void Launch(Guid runId)
    {
        lock (gate)
        {
            if (runs.ContainsKey(runId) || shutdown.IsCancellationRequested)
            {
                return;
            }

            var cancel = new CancellationTokenSource();
            var token = shutdown.Token;
            Live[runId] = 0;
            runs[runId] = (Task.Run(() => RunAsync(runId, token, cancel)), cancel);
        }
    }

    /// <summary>Asks the live run to stop (no-op when it is not live here).</summary>
    /// <param name="runId">The run.</param>
    /// <returns>True when a live run was told to stop.</returns>
    public bool Cancel(Guid runId)
    {
        lock (gate)
        {
            if (!runs.TryGetValue(runId, out var run))
            {
                return false;
            }

            run.Cancel.Cancel();
            return true;
        }
    }

    /// <summary>Completes when the run (if live) has ended. For tests and graceful waits.</summary>
    /// <param name="runId">The run.</param>
    /// <returns>The run's task.</returns>
    public Task WhenIdleAsync(Guid runId)
    {
        lock (gate)
        {
            return runs.TryGetValue(runId, out var run) ? run.Task : Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Only cancels: a run still unwinding releases its slot afterwards, so the primitives outlive this call
        // (the singleton is disposed when the process ends anyway).
        shutdown.Cancel();
    }

    private async Task RunAsync(Guid runId, CancellationToken stop, CancellationTokenSource cancel)
    {
        var acquired = false;
        try
        {
            using var either = CancellationTokenSource.CreateLinkedTokenSource(stop, cancel.Token);

            // Queued behind another wall's run (minutes): its heartbeat goes on meanwhile, so it does not look hung.
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(either.Token))
            {
                var heartbeat = job.HeartbeatAsync(runId, waiting.Token);
                try
                {
                    await slots.WaitAsync(either.Token);
                    acquired = true;
                }
                finally
                {
                    await waiting.CancelAsync();
                    await heartbeat;
                }
            }

            await job.RunAsync(runId, stop, cancel.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Shut down while queued or running: the run stays Running and resumes on the next start.
        }
        catch (OperationCanceledException)
        {
            // Cancelled while still queued: nothing ran yet.
            await job.MarkCancelledAsync(runId);
        }
        finally
        {
            if (acquired)
            {
                slots.Release();
            }

            lock (gate)
            {
                runs.Remove(runId);
                Live.TryRemove(runId, out _);
            }

            cancel.Dispose();
        }
    }
}
