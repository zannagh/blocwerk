// <copyright file="UnalignableHoldMatcher.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A <see cref="PositionHoldMatcher"/> that fails exactly like the real one does when it cannot line two photos
/// up (a thrown coarse-homography failure) for every pair <paramref name="fails"/> selects by image bytes.
/// </summary>
internal sealed class UnalignableHoldMatcher(Func<byte[], byte[], bool> fails) : IHoldOverlapMatcher
{
    private readonly PositionHoldMatcher inner = new();

    public int Calls { get; private set; }

    public HoldOverlapResult Match(
        byte[] leftImage,
        IReadOnlyList<MatcherHold> leftHolds,
        byte[] rightImage,
        IReadOnlyList<MatcherHold> rightHolds,
        HoldOverlapDirection direction,
        ILogger? diag = null,
        HoldOverlapSeed? seed = null)
    {
        Calls++;
        if (fails(leftImage, rightImage))
        {
            throw new InvalidOperationException("Coarse homography failed (too few texture matches: ratio matches 20, RANSAC inliers 9).");
        }

        return inner.Match(leftImage, leftHolds, rightImage, rightHolds, direction, diag, seed);
    }
}
