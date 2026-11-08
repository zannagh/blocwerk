// <copyright file="RemoteRecognitionExtensions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Probe and overlap pass for callers that await them: a recognition runner's job is awaited without a gate slot
/// (<see cref="IAsyncHoldPresenceProbe"/>, <see cref="IAsyncHoldOverlapMatcher"/>); anything else runs in the app off
/// the circuit, inside an awaited slot (<see cref="ImageRecognitionGateExtensions.RecogniseAsync"/>), as it always did.
/// </summary>
public static class RemoteRecognitionExtensions
{
    /// <summary>Scores <paramref name="queries"/>; see the class.</summary>
    /// <param name="probe">The probe.</param>
    /// <param name="gate">The gate, or null to run ungated.</param>
    /// <param name="operation">A short name for logs and the slot.</param>
    /// <param name="oldImage">The old photo.</param>
    /// <param name="newImage">The new photo.</param>
    /// <param name="queries">The spots.</param>
    /// <param name="ct">Cancels the wait for a slot or a runner.</param>
    /// <returns>Per query, the score or null.</returns>
    public static Task<IReadOnlyList<double?>> ScoreAsync(
        this IHoldPresenceProbe probe,
        IImageRecognitionGate? gate,
        string operation,
        byte[] oldImage,
        byte[] newImage,
        IReadOnlyList<PresenceQuery> queries,
        CancellationToken ct = default) =>
        probe is IAsyncHoldPresenceProbe remote
            ? remote.ScoreAsync(operation, oldImage, newImage, queries, ct)
            : gate.RecogniseAsync(operation, () => probe.Score(oldImage, newImage, queries), ct);

    /// <summary>The overlap pass between two panels; see the class.</summary>
    /// <param name="matcher">The matcher.</param>
    /// <param name="gate">The gate, or null to run ungated.</param>
    /// <param name="operation">A short name for logs and the slot.</param>
    /// <param name="pair">The two photos and their holds.</param>
    /// <param name="direction">Advisory overlap direction.</param>
    /// <param name="diag">Optional diagnostics sink.</param>
    /// <param name="seed">Optional seed.</param>
    /// <param name="ct">Cancels the wait for a slot or a runner.</param>
    /// <returns>The proposals.</returns>
    public static Task<HoldOverlapResult> MatchAsync(
        this IHoldOverlapMatcher matcher,
        IImageRecognitionGate? gate,
        string operation,
        MatcherPair pair,
        HoldOverlapDirection direction,
        ILogger? diag,
        HoldOverlapSeed? seed,
        CancellationToken ct = default) =>
        matcher is IAsyncHoldOverlapMatcher remote
            ? remote.MatchAsync(operation, pair, direction, diag, seed, ct)
            : gate.RecogniseAsync(
                operation, () => matcher.Match(pair.LeftImage, pair.LeftHolds, pair.RightImage, pair.RightHolds, direction, diag, seed), ct);
}
