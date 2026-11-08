// <copyright file="IAsyncHoldOverlapMatcher.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// An overlap matcher that may run elsewhere (a recognition runner). Awaited without a gate slot: only the work it does in
/// the app takes one, so waiting for a runner never keeps other recognition waiting.
/// </summary>
public interface IAsyncHoldOverlapMatcher
{
    /// <summary>Like <see cref="IHoldOverlapMatcher.Match"/>.</summary>
    /// <param name="operation">The gate slot's name when it runs in the app.</param>
    /// <param name="pair">The two photos and their holds.</param>
    /// <param name="direction">Advisory overlap direction.</param>
    /// <param name="diag">Optional diagnostics sink.</param>
    /// <param name="seed">Optional seed.</param>
    /// <param name="ct">Cancels the wait; a runner's job is then cancelled too.</param>
    /// <returns>The proposals.</returns>
    Task<HoldOverlapResult> MatchAsync(
        string operation, MatcherPair pair, HoldOverlapDirection direction, ILogger? diag, HoldOverlapSeed? seed, CancellationToken ct);
}
