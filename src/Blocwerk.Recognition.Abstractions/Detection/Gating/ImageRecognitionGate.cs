// <copyright file="ImageRecognitionGate.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Detection.Gating;

/// <summary>
/// <see cref="IImageRecognitionGate"/> on a <see cref="SemaphoreSlim"/>. The flow that holds a slot is remembered in an
/// <see cref="AsyncLocal{T}"/>, so recognition nested inside it (enrichment → outline session) never waits for its own
/// slot. Fails closed: a wait past the timeout throws instead of running without a slot.
/// </summary>
public sealed class ImageRecognitionGate : IImageRecognitionGate, IDisposable
{
    private readonly SemaphoreSlim slots;
    private readonly TimeSpan timeout;
    private readonly ILogger logger;

    /// <summary>Initializes a new instance of the <see cref="ImageRecognitionGate"/> class from the hold-detection settings.</summary>
    /// <param name="settings">Hold-detection settings (<see cref="HoldDetectionSettings.Enabled"/>, <see cref="HoldDetectionSettings.MaxConcurrentRuns"/>,
    /// <see cref="HoldDetectionSettings.GateTimeoutSeconds"/>).</param>
    /// <param name="logger">Logger.</param>
    public ImageRecognitionGate(HoldDetectionSettings settings, ILogger<ImageRecognitionGate> logger)
        : this(
            settings.MaxConcurrentRuns,
            TimeSpan.FromSeconds(settings.GateTimeoutSeconds),
            logger,
            settings.Enabled)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ImageRecognitionGate"/> class.</summary>
    /// <param name="maxConcurrentRuns">Runs that may hold a slot at once (at least 1).</param>
    /// <param name="timeout">How long a run waits for a slot before it fails as busy.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="enabled">Whether recognition is switched on; off, every call fails.</param>
    public ImageRecognitionGate(int maxConcurrentRuns, TimeSpan timeout, ILogger logger, bool enabled = true)
    {
        int max = Math.Max(1, maxConcurrentRuns);
        slots = new SemaphoreSlim(max, max);
        this.timeout = timeout;
        this.logger = logger;
        Enabled = enabled;
    }

    /// <inheritdoc/>
    public bool Enabled { get; }

    /// <summary>Gets the lease of the current flow, if any.</summary>
    internal static AsyncLocal<ImageRecognitionLease?> Current { get; } = new();

    private static bool Held => Current.Value is { IsHeld: true };

    /// <inheritdoc/>
    public async Task<T> RunAsync<T>(string operation, Func<Task<T>> work, CancellationToken ct = default)
    {
        using var lease = await EnterAsync(operation, ct);
        return await work();
    }

    /// <inheritdoc/>
    public Task<IDisposable> EnterAsync(string operation, CancellationToken ct = default)
    {
        if (Held)
        {
            return Task.FromResult(ImageRecognitionLease.Nested);
        }

        return Begin(operation).AcquireAsync(ct);
    }

    /// <inheritdoc/>
    public IDisposable Enter(string operation, CancellationToken ct = default)
    {
        if (Held)
        {
            return ImageRecognitionLease.Nested;
        }

        var lease = Begin(operation);
        lease.Acquire(ct);
        return lease;
    }

    /// <inheritdoc/>
    public void Dispose() => slots.Dispose();

    // Not async on purpose: the AsyncLocal set here stays visible to the caller's flow (an async method would undo it on return).
    private ImageRecognitionLease Begin(string operation)
    {
        if (!Enabled)
        {
            throw new ImageRecognitionUnavailableException(ImageRecognitionUnavailableException.DisabledMessage);
        }

        var lease = new ImageRecognitionLease(operation, slots, timeout, Current.Value, logger);
        Current.Value = lease;
        return lease;
    }
}
