// <copyright file="IImageRecognitionGate.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// The one process-wide limit on heavy image recognition (YOLO, enrichment, outline sessions, matchers): at most
/// <c>HoldDetection:MaxConcurrentRuns</c> of them hold decoded photos and model buffers at the same time, whether a
/// circuit or a background job asked. Reentrant: a call made while the same flow already holds the gate runs inside
/// that hold instead of waiting for itself, so enrichment can open an outline session without deadlocking.
/// <para>
/// Fails closed: nothing runs without a slot. A wait longer than <c>HoldDetection:GateTimeoutSeconds</c> throws an
/// <see cref="ImageRecognitionUnavailableException"/> ("busy, try again"), and so does every call while
/// <c>HoldDetection:Enabled</c> is false.
/// </para>
/// </summary>
public interface IImageRecognitionGate
{
    /// <summary>Gets a value indicating whether recognition is switched on (<c>HoldDetection:Enabled</c>).</summary>
    bool Enabled { get; }

    /// <summary>Runs <paramref name="work"/> once a slot is free; nested gated calls inside it pass straight through.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="operation">A short name for logs and metrics (detect, enrich, stage-panel, ...).</param>
    /// <param name="work">The recognition work.</param>
    /// <param name="ct">Cancels the wait for a slot.</param>
    /// <returns>The work's result.</returns>
    Task<T> RunAsync<T>(string operation, Func<Task<T>> work, CancellationToken ct = default);

    /// <summary>
    /// Takes a slot without blocking a thread, until the returned lease is disposed. Not an <c>async</c> method on
    /// purpose: the caller's flow counts as holding the gate from the moment the slot is granted, so sync recognition it
    /// runs afterwards (also inside <see cref="Task.Run(Action)"/>) passes straight through.
    /// </summary>
    /// <param name="operation">A short name for logs and metrics.</param>
    /// <param name="ct">Cancels the wait for a slot.</param>
    /// <returns>The lease; dispose it to give the slot back.</returns>
    Task<IDisposable> EnterAsync(string operation, CancellationToken ct = default);

    /// <summary>
    /// Takes a slot until the returned lease is disposed, blocking this thread until one is free. Only for sync code
    /// already off the request thread; everything a circuit or a job awaits uses <see cref="EnterAsync"/>.
    /// </summary>
    /// <param name="operation">A short name for logs and metrics.</param>
    /// <param name="ct">Cancels the wait for a slot.</param>
    /// <returns>The lease; dispose it to give the slot back.</returns>
    IDisposable Enter(string operation, CancellationToken ct = default);
}
