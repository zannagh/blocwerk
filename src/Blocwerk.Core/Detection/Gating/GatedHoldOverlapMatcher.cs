// <copyright file="GatedHoldOverlapMatcher.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Detection.Gating;

/// <summary>
/// The cross-panel matcher behind the <see cref="IImageRecognitionGate"/>. Always offers the carryover pass: it
/// forwards to the inner matcher's when there is one and falls back to <see cref="Match"/> as callers did before.
/// </summary>
public sealed class GatedHoldOverlapMatcher : IHoldOverlapMatcher, IHoldCarryMatcher
{
    private readonly IHoldOverlapMatcher inner;
    private readonly IImageRecognitionGate gate;

    /// <summary>Initializes a new instance of the <see cref="GatedHoldOverlapMatcher"/> class.</summary>
    /// <param name="inner">The matcher.</param>
    /// <param name="gate">The process-wide recognition gate.</param>
    public GatedHoldOverlapMatcher(IHoldOverlapMatcher inner, IImageRecognitionGate gate)
    {
        this.inner = inner;
        this.gate = gate;
    }

    /// <inheritdoc/>
    public HoldOverlapResult Match(
        byte[] leftImage,
        IReadOnlyList<MatcherHold> leftHolds,
        byte[] rightImage,
        IReadOnlyList<MatcherHold> rightHolds,
        HoldOverlapDirection direction,
        ILogger? diag = null,
        HoldOverlapSeed? seed = null)
    {
        using var lease = gate.Enter("overlap-match");
        return inner.Match(leftImage, leftHolds, rightImage, rightHolds, direction, diag, seed);
    }

    /// <inheritdoc/>
    public HoldOverlapResult MatchCarry(
        byte[] leftImage,
        IReadOnlyList<MatcherHold> leftHolds,
        byte[] rightImage,
        IReadOnlyList<MatcherHold> rightHolds,
        HoldOverlapDirection direction,
        ILogger? diag = null,
        HoldOverlapSeed? seed = null)
    {
        using var lease = gate.Enter("carry-match");
        return inner is IHoldCarryMatcher carry
            ? carry.MatchCarry(leftImage, leftHolds, rightImage, rightHolds, direction, diag, seed)
            : inner.Match(leftImage, leftHolds, rightImage, rightHolds, direction, diag, seed);
    }
}
