// <copyright file="MarkerSizingDecodeTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.HoldDetection.Tests.MarkerPlanning;

/// <summary>
/// The planner's smallest sizes at the planner's decode floor: printed plan markers (one-module quiet zone)
/// on a dark wall, rendered so the smallest is just over <see cref="MarkerDetectability.FillerMinPx"/>,
/// must still decode with sub-pixel corners. The real-photo study behind the floor is in
/// scratchpad/sizing-study (README).
/// </summary>
public class MarkerSizingDecodeTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.6)]
    public void SmallestPlanSizes_DecodeAtTheFillerFloor(double blurPx)
    {
        var plan = MarkerPhotoSim.TestPlan([30, 40, 50], holes: null);
        var dpi = (float)((MarkerDetectability.FillerMinPx + 0.5) / 30 * 25.4);

        var errors = MarkerPhotoSim.CornerErrors(plan, dpi, new PhotoDressing(null, 70, null, blurPx), MarkerDetectionOptions.Default);

        Assert.Equal(3, errors.Count);
        Assert.All(errors.Values, e => Assert.InRange(e, 0, 1.0));
    }
}
