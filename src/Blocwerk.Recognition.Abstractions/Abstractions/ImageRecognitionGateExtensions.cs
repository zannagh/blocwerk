// <copyright file="ImageRecognitionGateExtensions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>The awaitable way to run sync recognition code (sessions, matchers, probes) behind the gate.</summary>
public static class ImageRecognitionGateExtensions
{
    /// <summary>
    /// Awaits a slot, then runs <paramref name="work"/> on the thread pool inside it: neither the circuit nor a worker
    /// thread ever blocks on the gate, and the gated services <paramref name="work"/> calls see the slot as their own.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="gate">The gate, or null to run ungated (hosts and tests without recognition).</param>
    /// <param name="operation">A short name for logs and metrics.</param>
    /// <param name="work">The CPU-bound recognition work.</param>
    /// <param name="ct">Cancels the wait for a slot.</param>
    /// <returns>The work's result.</returns>
    public static async Task<T> RecogniseAsync<T>(this IImageRecognitionGate? gate, string operation, Func<T> work, CancellationToken ct = default)
    {
        using var lease = gate is null ? null : await gate.EnterAsync(operation, ct);
        return await Task.Run(work, ct);
    }
}
