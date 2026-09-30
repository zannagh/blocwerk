// <copyright file="PanelPositionNameTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Refresh;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>Panels are named from their place next to the centre panel, never as grid coordinates.</summary>
public class PanelPositionNameTests
{
    [Theory]
    [InlineData(0, 0, "Centre panel")]
    [InlineData(1, 0, "Right panel")]
    [InlineData(-1, 0, "Left panel")]
    [InlineData(0, -1, "Panel above")]
    [InlineData(0, 1, "Panel below")]
    [InlineData(2, 0, "Panel 2 to the right")]
    [InlineData(0, -2, "Panel 2 above")]
    [InlineData(-1, -1, "Panel above left")]
    [InlineData(2, 1, "Panel 1 below, 2 right")]
    public void Describe_NamesThePlaceNextToTheCentre(int col, int row, string expected)
    {
        Assert.Equal(expected, PanelPositionName.Describe(col, row));
    }

    [Fact]
    public void InSentence_StartsLowerCase()
    {
        Assert.Equal("the right panel", PanelPositionName.InSentence(1, 0));
        Assert.Equal("the panel above", PanelPositionName.InSentence(0, -1));
    }
}
