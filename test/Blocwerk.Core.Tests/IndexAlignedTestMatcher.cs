// <copyright file="IndexAlignedTestMatcher.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A deterministic stand-in for the OpenCV matcher: proposes left[i] ↔ right[i] for the overlapping prefix
/// and leaves the rest unmatched, so a session build runs end to end without native OpenCV. Records every
/// left image it was handed.
/// </summary>
internal sealed class IndexAlignedTestMatcher : IHoldOverlapMatcher
{
    public List<byte[]> LeftImages { get; } = [];

    public HoldOverlapResult Match(
        byte[] leftImage,
        IReadOnlyList<MatcherHold> leftHolds,
        byte[] rightImage,
        IReadOnlyList<MatcherHold> rightHolds,
        HoldOverlapDirection direction,
        ILogger? diag = null,
        HoldOverlapSeed? seed = null)
    {
        LeftImages.Add(leftImage);
        var n = Math.Min(leftHolds.Count, rightHolds.Count);
        var proposals = new List<HoldOverlapProposal>();
        for (var i = 0; i < n; i++)
        {
            proposals.Add(new HoldOverlapProposal(leftHolds[i].Id, rightHolds[i].Id, 0.9, false, 1.0, null));
        }

        return new HoldOverlapResult(
            proposals,
            leftHolds.Skip(n).Select(hold => hold.Id).ToList(),
            rightHolds.Skip(n).Select(hold => hold.Id).ToList());
    }
}
