// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Recognition.Worker.Runtime;

/// <summary>
/// Design §4, layer 3: reads the probe every interval while a job runs and cancels the job once memory passes
/// <c>JOB_MAX_MEMORY_MB</c>, so the job fails with <c>failureKind: "resource"</c> before the kernel kills the pod. Cancelling
/// stops the managed work only; when memory is still above the limit <c>grace</c> later (native work carries on),
/// <see cref="NotReleased"/> tells the caller to end the process.
/// </summary>
public sealed class MemoryWatchdog : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    private MemoryWatchdog(IMemoryProbe probe, long limitBytes, TimeSpan interval, TimeSpan grace, CancellationTokenSource job)
    {
        loop = limitBytes > 0 ? WatchAsync(probe, limitBytes, interval, grace, job) : Task.CompletedTask;
    }

    public bool Exceeded { get; private set; }

    /// <summary>Gets a value indicating whether memory stayed above the limit for the grace period after the cancel.</summary>
    public bool NotReleased { get; private set; }

    public long PeakBytes { get; private set; }

    public static MemoryWatchdog Start(IMemoryProbe probe, WorkerOptions options, CancellationTokenSource job) =>
        new(probe, options.JobMaxMemoryMb * 1024 * 1024, options.WatchdogInterval, options.MemoryReleaseGrace, job);

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await loop;
        stop.Dispose();
    }

    private async Task WatchAsync(IMemoryProbe probe, long limitBytes, TimeSpan interval, TimeSpan grace, CancellationTokenSource job)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                PeakBytes = Math.Max(PeakBytes, probe.CurrentBytes());
                if (PeakBytes > limitBytes)
                {
                    Exceeded = true;
                    await job.CancelAsync();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The job ended first.
            return;
        }

        // Not stopped by the job's end: the caller waits the grace out before it settles.
        var until = DateTime.UtcNow + grace;
        while (probe.CurrentBytes() > limitBytes)
        {
            if (DateTime.UtcNow >= until)
            {
                NotReleased = true;
                return;
            }

            await Task.Delay(interval, CancellationToken.None);
        }
    }
}
