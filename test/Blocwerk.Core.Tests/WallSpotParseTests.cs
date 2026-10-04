// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Web.Components.Shared;

namespace Blocwerk.Core.Tests;

public class WallSpotParseTests
{
    [Fact]
    public void Parse_AcceptsOrdinaryValues()
    {
        var spot = WallSpot.Parse("f1:120:-340.5:80");
        Assert.NotNull(spot);
        Assert.Equal(-340.5, spot!.B);
    }

    [Theory]
    [InlineData("0:1e308:1e308:1e308")]
    [InlineData("0:1000001:0:0")]
    [InlineData("0:0:-1000001:0")]
    [InlineData("0:0:0:Infinity")]
    [InlineData("0:NaN:0:0")]
    public void Parse_RejectsNonFiniteAndAbsurdValues(string query)
    {
        Assert.Null(WallSpot.Parse(query));
    }
}
