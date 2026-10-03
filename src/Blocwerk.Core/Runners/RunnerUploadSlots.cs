// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Runners;

/// <summary>
/// Bounds the result uploads streaming in at once: one per job and <see cref="GpuRunnerOptions.MaxConcurrentUploads"/>
/// server-wide (the pattern of <see cref="Capture.CaptureVideoUploadSlots"/>). Each upload may write up to
/// <see cref="GpuRunnerOptions.MaxResultBytes"/> to disk and holds the deploy gate while it streams. In-process, like
/// the rest of the single-instance queue.
/// </summary>
public sealed class RunnerUploadSlots(int global)
{
    private readonly object gate = new();
    private readonly HashSet<Guid> jobs = [];

    /// <summary>Uploads streaming right now.</summary>
    public int Active
    {
        get
        {
            lock (gate)
            {
                return jobs.Count;
            }
        }
    }

    /// <summary>
    /// A slot for <paramref name="jobId"/>, or null with <see cref="RunnerJobOutcome.UploadInProgress"/> (this job is
    /// already uploading) or <see cref="RunnerJobOutcome.ServerBusy"/> (the server is at its limit).
    /// </summary>
    public IDisposable? TryAcquire(Guid jobId, out RunnerJobOutcome refused)
    {
        lock (gate)
        {
            if (jobs.Contains(jobId))
            {
                refused = RunnerJobOutcome.UploadInProgress;
                return null;
            }

            if (jobs.Count >= global)
            {
                refused = RunnerJobOutcome.ServerBusy;
                return null;
            }

            jobs.Add(jobId);
            refused = RunnerJobOutcome.Ok;
            return new Slot(this, jobId);
        }
    }

    private void Release(Guid jobId)
    {
        lock (gate)
        {
            jobs.Remove(jobId);
        }
    }

    private sealed class Slot(RunnerUploadSlots owner, Guid jobId) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                owner.Release(jobId);
            }
        }
    }
}
