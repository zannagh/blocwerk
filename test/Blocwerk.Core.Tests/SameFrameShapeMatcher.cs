// <copyright file="SameFrameShapeMatcher.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The two photos show the wall from the same place: holds within <see cref="Tolerance"/> of each other are paired (nearest
/// first), and every old hold, outline included, is predicted where it was. Unlike <see cref="SameFrameHoldMatcher"/> it also
/// returns the warped outlines, as the real matcher does for an outlined old hold.
/// </summary>
internal sealed class SameFrameShapeMatcher : IHoldOverlapMatcher
{
    /// <summary>How far apart (normalized) two holds may be and still be paired.</summary>
    public double Tolerance { get; init; } = 0.02;

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
            var r = rightHolds
                .Where(c => !usedRight.Contains(c.Id) && Math.Abs(c.X - l.X) < Tolerance && Math.Abs(c.Y - l.Y) < Tolerance)
                .OrderBy(c => Math.Abs(c.X - l.X) + Math.Abs(c.Y - l.Y))
                .FirstOrDefault();
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
            rightHolds.Where(r => !usedRight.Contains(r.Id)).Select(r => r.Id).ToList(),
            leftHolds.Select(l => ((double X, double Y)?)(l.X, l.Y)).ToList(),
            leftHolds.Select(l => l.Shape).ToList());
    }
}
