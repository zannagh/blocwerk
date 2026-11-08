// <copyright file="IAsyncHoldPresenceProbe.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// A presence probe that may run elsewhere (a recognition runner). Awaited without a gate slot: only the work it does in
/// the app takes one, so waiting for a runner never keeps other recognition waiting.
/// </summary>
public interface IAsyncHoldPresenceProbe
{
    /// <summary>Like <see cref="IHoldPresenceProbe.Score"/>.</summary>
    /// <param name="operation">The gate slot's name when it runs in the app.</param>
    /// <param name="oldImage">The old photo.</param>
    /// <param name="newImage">The new photo.</param>
    /// <param name="queries">The spots.</param>
    /// <param name="ct">Cancels the wait; a runner's job is then cancelled too.</param>
    /// <returns>Per query, the score or null.</returns>
    Task<IReadOnlyList<double?>> ScoreAsync(
        string operation, byte[] oldImage, byte[] newImage, IReadOnlyList<PresenceQuery> queries, CancellationToken ct);
}
