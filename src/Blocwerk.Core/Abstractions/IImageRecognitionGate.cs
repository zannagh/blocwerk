// <copyright file="IImageRecognitionGate.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// The one process-wide limit on heavy image recognition (YOLO, enrichment, outline sessions, matchers): at most
/// <c>HoldDetection:MaxConcurrentRuns</c> of them hold decoded photos and model buffers at the same time, whether a
/// circuit or a background job asked. Reentrant: a call made while the same flow already holds the gate runs inside
/// that hold instead of waiting for itself, so enrichment can open an outline session without deadlocking.
/// </summary>
public interface IImageRecognitionGate
{
    /// <summary>Runs <paramref name="work"/> once a slot is free; nested gated calls inside it pass straight through.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="operation">A short name for logs and metrics (detect, enrich, stage-panel, ...).</param>
    /// <param name="work">The recognition work.</param>
    /// <param name="ct">Cancels the wait for a slot.</param>
    /// <returns>The work's result.</returns>
    Task<T> RunAsync<T>(string operation, Func<Task<T>> work, CancellationToken ct = default);

    /// <summary>
    /// Takes a slot until the returned lease is disposed, blocking until one is free. For sessions that keep a
    /// decoded photo across many calls; the hold covers the caller's flow from here on.
    /// </summary>
    /// <param name="operation">A short name for logs and metrics.</param>
    /// <returns>The lease; dispose it to give the slot back.</returns>
    IDisposable Enter(string operation);
}
