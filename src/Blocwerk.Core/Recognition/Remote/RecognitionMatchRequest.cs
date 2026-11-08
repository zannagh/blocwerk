// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// The <c>request</c> of a <c>match-panels</c> job (contracts <c>jobs/recognition/match-request</c>): the arguments of
/// <see cref="IHoldOverlapMatcher.Match"/> besides the images, points as <c>[x, y]</c>, point pairs as
/// <c>[leftX, leftY, rightX, rightY]</c>, a NaN fit residual as null.
/// </summary>
public static class RecognitionMatchRequest
{
    public static object Of(
        IReadOnlyList<MatcherHold> leftHolds, IReadOnlyList<MatcherHold> rightHolds, HoldOverlapDirection direction, HoldOverlapSeed? seed) => new
    {
        version = 1,
        leftHolds = leftHolds.Select(Hold),
        rightHolds = rightHolds.Select(Hold),
        direction,
        seed = seed is null ? null : Seed(seed),
    };

    private static object Hold(MatcherHold h) => new
    {
        id = h.Id,
        x = h.X,
        y = h.Y,
        sizeNorm = h.SizeNorm,
        color = h.Color,
        shape = h.Shape?.Select(p => new[] { p.X, p.Y }),
    };

    private static object Seed(HoldOverlapSeed s) => new
    {
        source = s.Source,
        homography = s.Homography,
        markerCount = s.MarkerCount,
        facetId = s.FacetId,
        fitRmsPx = double.IsFinite(s.FitRmsPx) ? s.FitRmsPx : (double?)null,
        measuredPairs = Pairs(s.MeasuredPairs),
        priorPairs = Pairs(s.PriorPairs),
        anchors = s.Anchors,
    };

    private static IEnumerable<double[]> Pairs(IEnumerable<HoldOverlapPointPair> pairs) =>
        pairs.Select(p => new[] { p.LeftX, p.LeftY, p.RightX, p.RightY }).Where(p => p.All(double.IsFinite));
}
