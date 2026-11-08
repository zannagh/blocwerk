// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Recognition.Worker.Runtime;

/// <summary>
/// Design §4, layer 3: reads the probe every interval while a job runs and cancels the job once memory passes
/// <c>JOB_MAX_MEMORY_MB</c>, so the job fails with <c>failureKind: "resource"</c> before the kernel kills the pod.
/// </summary>
public sealed class MemoryWatchdog : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    private MemoryWatchdog(IMemoryProbe probe, long limitBytes, TimeSpan interval, CancellationTokenSource job)
    {
        loop = limitBytes > 0 ? WatchAsync(probe, limitBytes, interval, job) : Task.CompletedTask;
    }

    public bool Exceeded { get; private set; }

    public long PeakBytes { get; private set; }

    public static MemoryWatchdog Start(IMemoryProbe probe, long limitMb, TimeSpan interval, CancellationTokenSource job) =>
        new(probe, limitMb * 1024 * 1024, interval, job);

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await loop;
        stop.Dispose();
    }

    private async Task WatchAsync(IMemoryProbe probe, long limitBytes, TimeSpan interval, CancellationTokenSource job)
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
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The job ended first.
        }
    }
}
