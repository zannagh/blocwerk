// <copyright file="MarkerFootprintTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;
using static Blocwerk.Core.Tests.MarkerPlanning.PlanFixtures;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>The wall area markers take — the owner's real cost — against a one-size plan.</summary>
public class MarkerFootprintTests
{
    [Fact]
    public void PlainPrint_CountsAOneModuleQuietZone()
    {
        Assert.Equal(80, MarkerFootprint.CutOutSideMm(60, null), 6);
        var plan = Plan([Rect(0, 3000, 3000)], [new PlanMarker(0, 0, 500, 500, 60, MarkerRole.Corner), new PlanMarker(1, 0, 1500, 500, 30, MarkerRole.Filler)]);

        var footprint = MarkerFootprint.Of(plan);

        Assert.Equal(2, footprint.MarkerCount);
        Assert.Equal((80 * 80 + 40 * 40) / 100.0, footprint.AreaCm2, 6);
        Assert.Equal(60, footprint.UniformSizeMm);
        Assert.Equal(2 * 80 * 80 / 100.0, footprint.UniformAreaCm2, 6);
        Assert.Equal(0.375, footprint.Saving, 6);
    }

    [Fact]
    public void MountingHoles_UseTheirBorder()
    {
        var print = new PrintOptions(MountingHoles.Default);

        Assert.Equal(40 + (2 * MountingHoleLayout.BorderMm(MountingHoles.Default)), MarkerFootprint.CutOutSideMm(40, print), 6);
    }

    [Fact]
    public void EmptyPlan_HasNoFootprint() => Assert.Equal(0, MarkerFootprint.Of(Plan([Rect(0, 1000, 1000)])).AreaCm2);
}
