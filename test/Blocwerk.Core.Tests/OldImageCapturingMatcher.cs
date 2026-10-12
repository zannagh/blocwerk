// <copyright file="OldImageCapturingMatcher.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Tests;

/// <summary>Records the left (old) image and holds of every matcher call; proposes nothing.</summary>
internal sealed class OldImageCapturingMatcher : IHoldOverlapMatcher
{
    public List<(byte[] LeftImage, IReadOnlyList<MatcherHold> LeftHolds)> Calls { get; } = [];

    public HoldOverlapResult Match(
        byte[] leftImage,
        IReadOnlyList<MatcherHold> leftHolds,
        byte[] rightImage,
        IReadOnlyList<MatcherHold> rightHolds,
        HoldOverlapDirection direction,
        ILogger? diag = null,
        HoldOverlapSeed? seed = null)
    {
        Calls.Add((leftImage, leftHolds));
        return new HoldOverlapResult([], leftHolds.Select(x => x.Id).ToList(), rightHolds.Select(x => x.Id).ToList());
    }
}
