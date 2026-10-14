// <copyright file="IHoldBatchOutlineSession.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// An outline session that can outline the holds of its photo together, sharing work between them and using the
/// detector boxes of the seeds that have one.
/// </summary>
public interface IHoldBatchOutlineSession : IHoldOutlineSession
{
    /// <summary>Outlines the given holds together.</summary>
    /// <param name="seeds">The seeds.</param>
    /// <returns>One result per seed, in order.</returns>
    IReadOnlyList<HoldOutlineResult> OutlineAll(IReadOnlyList<HoldSeed> seeds);
}

/// <summary>Outlining many holds on any session.</summary>
public static class HoldOutlineSessionExtensions
{
    /// <summary>Outlines the holds together where the session can, else one by one.</summary>
    /// <param name="session">The session.</param>
    /// <param name="seeds">The seeds.</param>
    /// <returns>One result per seed, in order.</returns>
    public static IReadOnlyList<HoldOutlineResult> OutlineEach(this IHoldOutlineSession session, IReadOnlyList<HoldSeed> seeds)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(seeds);
        return session is IHoldBatchOutlineSession batch ? batch.OutlineAll(seeds) : [.. seeds.Select(session.Outline)];
    }
}
