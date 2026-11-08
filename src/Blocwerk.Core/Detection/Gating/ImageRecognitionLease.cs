// <copyright file="ImageRecognitionLease.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Diagnostics;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Detection.Gating;

/// <summary>
/// One slot of the <see cref="ImageRecognitionGate"/>, from the moment it is granted until it is disposed. Measures the
/// process's resident memory (RSS) and managed heap at both ends, records them next to the recognition metrics and logs
/// a run that grew RSS by <see cref="LargeGrowthBytes"/> or more at Information.
/// </summary>
internal sealed class ImageRecognitionLease : IDisposable
{
    /// <summary>RSS growth from which a run is logged at Information instead of Debug.</summary>
    public const long LargeGrowthBytes = 256L * 1024 * 1024;

    private const double MiB = 1024 * 1024;

    private readonly string operation;
    private readonly SemaphoreSlim? slots;
    private readonly TimeSpan timeout;
    private readonly ImageRecognitionLease? previous;
    private readonly ILogger? logger;
    private readonly long created;
    private long start;
    private long rssBefore;
    private long heapBefore;
    private double waitMs;
    private bool acquired;
    private int released;

    /// <summary>Initializes a new instance of the <see cref="ImageRecognitionLease"/> class, not yet granted.</summary>
    /// <param name="operation">The run's name.</param>
    /// <param name="slots">The gate's semaphore.</param>
    /// <param name="timeout">How long to wait for a slot before failing as busy.</param>
    /// <param name="previous">The flow's lease before this one, restored on dispose.</param>
    /// <param name="logger">Logger.</param>
    public ImageRecognitionLease(string operation, SemaphoreSlim slots, TimeSpan timeout, ImageRecognitionLease? previous, ILogger? logger)
    {
        this.operation = operation;
        this.slots = slots;
        this.timeout = timeout;
        this.previous = previous;
        this.logger = logger;
        created = Stopwatch.GetTimestamp();
    }

    // The nested lease: already released, so disposing it does nothing.
    private ImageRecognitionLease()
    {
        operation = string.Empty;
        released = 1;
    }

    /// <summary>Gets the lease of a gated call nested in a run that already holds a slot: nothing to measure or release.</summary>
    public static IDisposable Nested { get; } = new ImageRecognitionLease();

    /// <summary>Gets a value indicating whether this lease holds its slot right now.</summary>
    public bool IsHeld => Volatile.Read(ref acquired) && Volatile.Read(ref released) == 0;

    /// <summary>Waits for the slot without blocking a thread.</summary>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>This lease, holding its slot.</returns>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        bool granted;
        try
        {
            granted = await slots!.WaitAsync(timeout, ct);
        }
        catch
        {
            // Cancelled: the flow must not count as holding the gate.
            Volatile.Write(ref released, 1);
            throw;
        }

        Granted(granted);
        return this;
    }

    /// <summary>Waits for the slot on this thread.</summary>
    /// <param name="ct">Cancels the wait.</param>
    public void Acquire(CancellationToken ct)
    {
        bool granted;
        try
        {
            granted = slots!.Wait(timeout, ct);
        }
        catch
        {
            Volatile.Write(ref released, 1);
            throw;
        }

        Granted(granted);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) != 0)
        {
            return;
        }

        if (ImageRecognitionGate.Current.Value == this)
        {
            ImageRecognitionGate.Current.Value = previous;
        }

        slots?.Release();
        Report();
    }

    private void Granted(bool granted)
    {
        waitMs = Stopwatch.GetElapsedTime(created).TotalMilliseconds;
        if (!granted)
        {
            Volatile.Write(ref released, 1);
            logger?.LogWarning("Image recognition {Operation} gave up after waiting {WaitMs:F0} ms for a free slot", operation, waitMs);
            throw new ImageRecognitionUnavailableException(ImageRecognitionUnavailableException.BusyMessage);
        }

        rssBefore = Environment.WorkingSet;
        heapBefore = GC.GetTotalMemory(false);
        start = Stopwatch.GetTimestamp();
        Volatile.Write(ref acquired, true);
    }

    private void Report()
    {
        long rssAfter = Environment.WorkingSet;
        long heapAfter = GC.GetTotalMemory(false);
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        BlocwerkMetrics.RecordImageRecognitionMemory(operation, rssAfter, rssAfter - rssBefore, heapAfter, waitMs);

        var level = rssAfter - rssBefore >= LargeGrowthBytes ? LogLevel.Information : LogLevel.Debug;
        logger?.Log(
            level,
            "Image recognition {Operation} took {Ms:F0} ms after waiting {WaitMs:F0} ms: RSS {RssBefore:F0} → {RssAfter:F0} MiB, GC heap {HeapBefore:F0} → {HeapAfter:F0} MiB",
            operation, ms, waitMs, rssBefore / MiB, rssAfter / MiB, heapBefore / MiB, heapAfter / MiB);
    }
}
