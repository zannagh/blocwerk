// <copyright file="IHoldOutlineBatch.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Outlines many holds on one photo in a single call: the stateless form of <see cref="IHoldOutlineService"/> that can
/// run as one job (photo + seeds in, geometry out). The caller keeps all planning and writes.
/// </summary>
public interface IHoldOutlineBatch
{
    /// <summary>Decodes <paramref name="photo"/> once and outlines every seed on it.</summary>
    /// <param name="photo">The encoded photo; seed coordinates are in its pixel grid.</param>
    /// <param name="seeds">The seeds.</param>
    /// <param name="options">How the seeds are outlined.</param>
    /// <param name="ct">Cancels the wait for a recognition slot.</param>
    /// <returns>The decoded photo size and one outline per seed, in seed order.</returns>
    /// <exception cref="ArgumentException">The bytes could not be decoded as an image.</exception>
    Task<HoldOutlineBatchResult> OutlineAsync(
        byte[] photo,
        IReadOnlyList<HoldSeed> seeds,
        HoldOutlineBatchOptions options,
        CancellationToken ct = default);
}
