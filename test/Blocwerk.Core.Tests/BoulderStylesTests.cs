// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Tests;

/// <summary>The fixed style set: its names, URL values, counting and the bits it refuses to store.</summary>
public sealed class BoulderStylesTests
{
    [Fact]
    public void TheSetIsTheSixStyles_InCardOrder() =>
        Assert.Equal(["Tension", "Dynamic", "Funky", "Endurance", "Power", "Technical"], BoulderStyles.All.Select(BoulderStyles.Label));

    [Theory]
    [InlineData("tension", BoulderStyle.Tension)]
    [InlineData("POWER", BoulderStyle.Power)]
    [InlineData(" technical ", BoulderStyle.Technical)]
    public void ASlugNamesItsStyle(string slug, BoulderStyle expected) => Assert.Equal(expected, BoulderStyles.FromSlug(slug));

    [Theory]
    [InlineData("all")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void AnythingElseIsNoSingleStyle(string? slug) => Assert.Null(BoulderStyles.FromSlug(slug));

    [Theory]
    [InlineData("ALL", "all")]
    [InlineData("Power", "power")]
    [InlineData("x&view=y", null)]
    [InlineData(null, null)]
    public void OnlyAKnownValueSurvivesIntoALink(string? raw, string? expected) => Assert.Equal(expected, BoulderStyles.QueryValue(raw));

    [Fact]
    public void NamesRoundTrip_AndNoStylesIsLeftOutOfTheDocument()
    {
        var both = BoulderStyle.Funky | BoulderStyle.Endurance;
        Assert.Equal(both, BoulderStyles.FromNames(BoulderStyles.ToNames(both)));
        Assert.Null(BoulderStyles.ToNames(BoulderStyle.None));
        Assert.Equal(BoulderStyle.None, BoulderStyles.FromNames(["Bogus"]));
    }

    [Fact]
    public void AStrayBitIsDropped() => Assert.Equal(BoulderStyle.Power, BoulderStyles.Normalize((BoulderStyle)(16 | 1024)));

    [Fact]
    public void ABoulderCountsOnceUnderEachOfItsStyles()
    {
        var counts = BoulderStyles.CountBy([BoulderStyle.Tension | BoulderStyle.Power, BoulderStyle.Power, BoulderStyle.None]);
        Assert.Equal(1, counts[BoulderStyle.Tension]);
        Assert.Equal(2, counts[BoulderStyle.Power]);
        Assert.Equal(0, counts[BoulderStyle.Funky]);
    }
}
