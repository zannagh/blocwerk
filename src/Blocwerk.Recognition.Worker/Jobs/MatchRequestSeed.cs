// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary>A <see cref="HoldOverlapSeed"/> on the wire: point pairs as <c>[leftX, leftY, rightX, rightY]</c>, NaN as null.</summary>
public sealed record MatchRequestSeed(
    HoldOverlapSeedSource Source,
    IReadOnlyList<double>? Homography = null,
    int MarkerCount = 0,
    string? FacetId = null,
    double? FitRmsPx = null,
    double[][]? MeasuredPairs = null,
    double[][]? PriorPairs = null,
    IReadOnlyList<HoldOverlapAnchor>? Anchors = null)
{
    public HoldOverlapSeed ToSeed() => new()
    {
        Source = Source,
        Homography = Homography,
        MarkerCount = MarkerCount,
        FacetId = FacetId,
        FitRmsPx = FitRmsPx ?? double.NaN,
        MeasuredPairs = Pairs(MeasuredPairs),
        PriorPairs = Pairs(PriorPairs),
        Anchors = Anchors ?? [],
    };

    private static List<HoldOverlapPointPair> Pairs(double[][]? pairs) =>
        pairs?.Select(p => new HoldOverlapPointPair(p[0], p[1], p[2], p[3])).ToList() ?? [];
}
