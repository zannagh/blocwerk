// <copyright file="WallShapeRecognitionRunner.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Hosts the background shape-recognition runs, one per wall-update session at most. A singleton so a run
/// outlives the circuit or HTTP request that started it; it reads and writes through
/// <see cref="RootDbContextFactory"/> because no user session exists by then — every authorisation check
/// happened in <see cref="WallUpdateShapeService"/> before <see cref="Launch"/>. In-process only (the app
/// is single-instance): a restart loses the live run, which the status then reports as interrupted.
/// </summary>
public sealed class WallShapeRecognitionRunner
{
    /// <summary>
    /// Runs that may outline at once, server-wide. Each is OpenCV work over full panel photos; anyone may
    /// create walls and start updates, so without a cap N walls meant N concurrent runs. Further runs
    /// queue (they count as running, and can be cancelled while they wait).
    /// </summary>
    public const int MaxConcurrentRuns = 2;

    private readonly SemaphoreSlim slots = new(MaxConcurrentRuns, MaxConcurrentRuns);
    private readonly object gate = new();
    private readonly Dictionary<Guid, (Task Task, CancellationTokenSource Cts)> runs = [];
    private readonly RootDbContextFactory factory;
    private readonly BlocwerkSettings settings;
    private readonly ILogger<WallShapeRecognitionRunner> logger;
    private readonly IHoldOutlineService? outlineService;

    public WallShapeRecognitionRunner(
        RootDbContextFactory factory,
        BlocwerkSettings settings,
        ILogger<WallShapeRecognitionRunner> logger,
        IHoldOutlineService? outlineService = null)
    {
        this.factory = factory;
        this.settings = settings;
        this.logger = logger;
        this.outlineService = outlineService;
    }

    /// <summary>Whether outlines can be recognised on this server (outliner present and not switched off).</summary>
    public bool Available => settings.HoldDetection.OutlinesEnabled && outlineService is not null;

    public bool IsRunning(Guid sessionId)
    {
        lock (gate)
        {
            return runs.ContainsKey(sessionId);
        }
    }

    /// <summary>Starts the run for the session unless one is already alive. The session must already be marked Running.</summary>
    public void Launch(Guid sessionId)
    {
        if (outlineService is null)
        {
            throw new InvalidOperationException("Outline detection is not available on this server.");
        }

        lock (gate)
        {
            if (runs.ContainsKey(sessionId))
            {
                return;
            }

            var cts = new CancellationTokenSource();
            var job = new WallShapeRecognitionJob(factory, outlineService, logger);
            var task = Task.Run(async () =>
            {
                var acquired = false;
                try
                {
                    await slots.WaitAsync(cts.Token);
                    acquired = true;
                    await job.RunAsync(sessionId, cts.Token);
                }
                catch (OperationCanceledException) when (!acquired)
                {
                    // Cancelled (skipped) while still queued: nothing ran; the caller sets the status.
                }
                finally
                {
                    if (acquired)
                    {
                        slots.Release();
                    }

                    lock (gate)
                    {
                        runs.Remove(sessionId);
                    }

                    cts.Dispose();
                }
            });
            runs[sessionId] = (task, cts);
        }
    }

    /// <summary>Stops a live run (no-op when none). The caller sets the resulting status.</summary>
    public void Cancel(Guid sessionId)
    {
        lock (gate)
        {
            if (runs.TryGetValue(sessionId, out var run))
            {
                run.Cts.Cancel();
            }
        }
    }

    /// <summary>Completes when the session's run (if any) has ended. For tests and graceful waits.</summary>
    public Task WhenIdleAsync(Guid sessionId)
    {
        lock (gate)
        {
            return runs.TryGetValue(sessionId, out var run) ? run.Task : Task.CompletedTask;
        }
    }
}
