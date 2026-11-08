// <copyright file="GatedHoldOutlineSession.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Detection.Gating;

/// <summary>
/// An outline session that gives its gate slot back when it is disposed. Keeps the inner session's joint outlining
/// (the segmenter's batch path) reachable through <see cref="HoldOutlineSessionExtensions.OutlineEach"/>.
/// </summary>
internal sealed class GatedHoldOutlineSession : IHoldBatchOutlineSession
{
    private readonly IHoldOutlineSession inner;
    private readonly IDisposable lease;

    /// <summary>Initializes a new instance of the <see cref="GatedHoldOutlineSession"/> class.</summary>
    /// <param name="inner">The session.</param>
    /// <param name="lease">The gate slot it holds.</param>
    public GatedHoldOutlineSession(IHoldOutlineSession inner, IDisposable lease)
    {
        this.inner = inner;
        this.lease = lease;
    }

    /// <inheritdoc/>
    public int ImageWidth => inner.ImageWidth;

    /// <inheritdoc/>
    public int ImageHeight => inner.ImageHeight;

    /// <inheritdoc/>
    public HoldOutlineResult Outline(HoldSeed seed) => inner.Outline(seed);

    /// <inheritdoc/>
    public IReadOnlyList<HoldOutlineResult> OutlineAll(IReadOnlyList<HoldSeed> seeds) => inner.OutlineEach(seeds);

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            inner.Dispose();
        }
        finally
        {
            lease.Dispose();
        }
    }
}
