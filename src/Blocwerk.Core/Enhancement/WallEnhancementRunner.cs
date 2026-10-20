// <copyright file="WallEnhancementRunner.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// Hosts the background enhancement runs, one per run at most. A singleton so a run outlives the circuit that started
/// it; every check happened in <see cref="WallEnhancementService"/> before <see cref="Launch"/>. In-process only (the app
/// is single-instance): a restart stops the live run, and <see cref="WallEnhancementResumer"/> starts it again.
/// </summary>
public sealed class WallEnhancementRunner(WallEnhancementJob job) : IDisposable
{
    /// <summary>Runs that may work at once, server-wide (each traces photos with OpenCV); further runs queue.</summary>
    public const int MaxConcurrentRuns = 1;

    private readonly SemaphoreSlim slots = new(MaxConcurrentRuns, MaxConcurrentRuns);
    private readonly CancellationTokenSource shutdown = new();
    private readonly object gate = new();
    private readonly Dictionary<Guid, Task> runs = [];

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

            var token = shutdown.Token;
            runs[runId] = Task.Run(() => RunAsync(runId, token));
        }
    }

    /// <summary>Completes when the run (if live) has ended. For tests and graceful waits.</summary>
    /// <param name="runId">The run.</param>
    /// <returns>The run's task.</returns>
    public Task WhenIdleAsync(Guid runId)
    {
        lock (gate)
        {
            return runs.TryGetValue(runId, out var task) ? task : Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Only cancels: a run still unwinding releases its slot afterwards, so the primitives outlive this call
        // (the singleton is disposed when the process ends anyway).
        shutdown.Cancel();
    }

    private async Task RunAsync(Guid runId, CancellationToken ct)
    {
        var acquired = false;
        try
        {
            await slots.WaitAsync(ct);
            acquired = true;
            await job.RunAsync(runId, ct);
        }
        catch (OperationCanceledException)
        {
            // Shut down while queued or running: the run stays Running and resumes on the next start.
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
            }
        }
    }
}
