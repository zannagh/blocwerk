// <copyright file="PositionHoldMatcher.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Tests;

/// <summary>Pairs holds that sit on the same spot (within 0.01); everything else is unmatched.</summary>
internal sealed class PositionHoldMatcher : IHoldOverlapMatcher
{
    public HoldOverlapResult Match(
        byte[] leftImage,
        IReadOnlyList<MatcherHold> leftHolds,
        byte[] rightImage,
        IReadOnlyList<MatcherHold> rightHolds,
        HoldOverlapDirection direction,
        ILogger? diag = null,
        HoldOverlapSeed? seed = null)
    {
        var proposals = new List<HoldOverlapProposal>();
        var usedRight = new HashSet<int>();
        foreach (var l in leftHolds)
        {
            var r = rightHolds.FirstOrDefault(c => !usedRight.Contains(c.Id)
                && Math.Abs(c.X - l.X) < 0.01 && Math.Abs(c.Y - l.Y) < 0.01);
            if (r is not null)
            {
                usedRight.Add(r.Id);
                proposals.Add(new HoldOverlapProposal(l.Id, r.Id, 0.9, false, 1.0, null));
            }
        }

        var matchedLeft = proposals.Select(p => p.LeftHoldId).ToHashSet();
        return new HoldOverlapResult(
            proposals,
            leftHolds.Where(l => !matchedLeft.Contains(l.Id)).Select(l => l.Id).ToList(),
            rightHolds.Where(r => !usedRight.Contains(r.Id)).Select(r => r.Id).ToList());
    }
}
