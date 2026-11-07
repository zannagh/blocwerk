// <copyright file="ImageRecognitionGate.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Diagnostics;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Detection.Gating;

/// <summary>
/// <see cref="IImageRecognitionGate"/> on a <see cref="SemaphoreSlim"/>. The flow that holds a slot is remembered in an
/// <see cref="AsyncLocal{T}"/>, so recognition nested inside it (enrichment → outline session) never waits for its own
/// slot. A wait longer than the timeout is logged and the run goes ahead without a slot: a missed nesting then costs
/// memory headroom, never a hung wall update.
/// </summary>
public sealed class ImageRecognitionGate : IImageRecognitionGate, IDisposable
{
    private readonly SemaphoreSlim slots;
    private readonly TimeSpan timeout;
    private readonly ILogger logger;

    /// <summary>Initializes a new instance of the <see cref="ImageRecognitionGate"/> class from the app settings.</summary>
    /// <param name="settings">App settings (<see cref="HoldDetectionSettings.MaxConcurrentRuns"/>, <see cref="HoldDetectionSettings.GateTimeoutSeconds"/>).</param>
    /// <param name="logger">Logger.</param>
    public ImageRecognitionGate(BlocwerkSettings settings, ILogger<ImageRecognitionGate> logger)
        : this(settings.HoldDetection.MaxConcurrentRuns, TimeSpan.FromSeconds(settings.HoldDetection.GateTimeoutSeconds), logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ImageRecognitionGate"/> class.</summary>
    /// <param name="maxConcurrentRuns">Runs that may hold a slot at once (at least 1).</param>
    /// <param name="timeout">How long a run waits before it goes ahead without a slot.</param>
    /// <param name="logger">Logger.</param>
    public ImageRecognitionGate(int maxConcurrentRuns, TimeSpan timeout, ILogger logger)
    {
        int max = Math.Max(1, maxConcurrentRuns);
        slots = new SemaphoreSlim(max, max);
        this.timeout = timeout;
        this.logger = logger;
    }

    /// <summary>Gets the lease of the current flow, if any.</summary>
    internal static AsyncLocal<ImageRecognitionLease?> Current { get; } = new();

    private static bool Held => Current.Value is { IsReleased: false };

    /// <inheritdoc/>
    public async Task<T> RunAsync<T>(string operation, Func<Task<T>> work, CancellationToken ct = default)
    {
        if (Held)
        {
            return await work();
        }

        long waitStart = Stopwatch.GetTimestamp();
        bool acquired = await slots.WaitAsync(timeout, ct);
        using var lease = Begin(operation, acquired, waitStart);
        return await work();
    }

    /// <inheritdoc/>
    public IDisposable Enter(string operation)
    {
        if (Held)
        {
            return ImageRecognitionLease.Nested;
        }

        long waitStart = Stopwatch.GetTimestamp();
        bool acquired = slots.Wait(timeout);
        return Begin(operation, acquired, waitStart);
    }

    /// <inheritdoc/>
    public void Dispose() => slots.Dispose();

    // Not async on purpose: the AsyncLocal set here stays visible to the caller's flow (an async method would undo it on return).
    private ImageRecognitionLease Begin(string operation, bool acquired, long waitStart)
    {
        var waited = Stopwatch.GetElapsedTime(waitStart);
        if (!acquired)
        {
            logger.LogWarning(
                "Image recognition {Operation} waited {Waited:F0} s for a free slot and runs without one", operation, waited.TotalSeconds);
        }

        var lease = new ImageRecognitionLease(operation, acquired ? slots : null, Current.Value, waited.TotalMilliseconds, logger);
        Current.Value = lease;
        return lease;
    }
}
