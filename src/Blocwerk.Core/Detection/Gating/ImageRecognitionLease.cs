// <copyright file="ImageRecognitionLease.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Diagnostics;
using Blocwerk.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Detection.Gating;

/// <summary>
/// One slot of the <see cref="ImageRecognitionGate"/>, held from the start of a run until it is disposed. Measures the
/// process's resident memory (RSS) and managed heap at both ends, records them next to the recognition metrics and logs
/// a run that grew RSS by <see cref="LargeGrowthBytes"/> or more at Information.
/// </summary>
internal sealed class ImageRecognitionLease : IDisposable
{
    /// <summary>RSS growth from which a run is logged at Information instead of Debug.</summary>
    public const long LargeGrowthBytes = 256L * 1024 * 1024;

    private const double MiB = 1024 * 1024;

    private readonly string operation;
    private readonly SemaphoreSlim? slot;
    private readonly ImageRecognitionLease? previous;
    private readonly double waitMs;
    private readonly ILogger? logger;
    private readonly long start;
    private readonly long rssBefore;
    private readonly long heapBefore;
    private int released;

    /// <summary>Initializes a new instance of the <see cref="ImageRecognitionLease"/> class and measures the "before" side.</summary>
    /// <param name="operation">The run's name.</param>
    /// <param name="slot">The semaphore to release, or null when the run went ahead without a slot.</param>
    /// <param name="previous">The flow's lease before this one, restored on dispose.</param>
    /// <param name="waitMs">How long the run waited for its slot.</param>
    /// <param name="logger">Logger.</param>
    public ImageRecognitionLease(string operation, SemaphoreSlim? slot, ImageRecognitionLease? previous, double waitMs, ILogger? logger)
    {
        this.operation = operation;
        this.slot = slot;
        this.previous = previous;
        this.waitMs = waitMs;
        this.logger = logger;
        rssBefore = ResidentBytes();
        heapBefore = GC.GetTotalMemory(false);
        start = Stopwatch.GetTimestamp();
    }

    // The nested lease: already released, so disposing it does nothing.
    private ImageRecognitionLease()
    {
        operation = string.Empty;
        released = 1;
    }

    /// <summary>Gets the lease of a gated call nested in a run that already holds a slot: nothing to measure or release.</summary>
    public static IDisposable Nested { get; } = new ImageRecognitionLease();

    /// <summary>Gets a value indicating whether the slot was given back.</summary>
    public bool IsReleased => Volatile.Read(ref released) != 0;

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

        slot?.Release();
        Report();
    }

    private static long ResidentBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }

    private void Report()
    {
        long rssAfter = ResidentBytes();
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
