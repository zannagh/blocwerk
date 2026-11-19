// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Tests;

/// <summary>The boulder browser's levels and their URLs, which reload, back and the boulder page's back link rely on.</summary>
public sealed class BoulderBrowseTests
{
    [Theory]
    [InlineData(null, null, null, BoulderBrowseLevel.Landing)]
    [InlineData("holds", null, null, BoulderBrowseLevel.HoldTypes)]
    [InlineData("STYLES", null, null, BoulderBrowseLevel.Styles)]
    [InlineData(null, "all", null, BoulderBrowseLevel.List)]
    [InlineData(null, "power", null, BoulderBrowseLevel.List)]
    [InlineData(null, null, "crimp", BoulderBrowseLevel.List)]
    [InlineData("bogus", "bogus", "bogus", BoulderBrowseLevel.Landing)]
    public void TheUrlNamesTheLevel(string? browse, string? style, string? hold, BoulderBrowseLevel expected) =>
        Assert.Equal(expected, BoulderBrowse.From(browse, style, hold).Level);

    [Fact]
    public void AList_GoesBackToItsCardPage_AndACardPageToTheLanding()
    {
        Assert.Equal(BoulderBrowseLevel.Styles, BoulderBrowse.From(null, "power", null).Up.Level);
        Assert.Equal(BoulderBrowseLevel.HoldTypes, BoulderBrowse.From(null, null, "crimp").Up.Level);
        Assert.Equal(BoulderBrowseLevel.Landing, BoulderBrowse.All.Up.Level);
        Assert.Equal(BoulderBrowseLevel.Landing, BoulderBrowse.From("holds", null, null).Up.Level);
    }

    [Fact]
    public void AListCarriesItsLevel_IntoTheBoulderLink()
    {
        Assert.Equal("hold=crimp", BoulderBrowse.From(null, null, "Crimp").ListQuery);
        Assert.Equal("style=warmup", BoulderBrowse.From(null, "warmup", null).ListQuery);
        Assert.Equal("style=all", BoulderBrowse.All.ListQuery);
        Assert.Null(BoulderBrowse.Landing.ListQuery);
    }

    [Fact]
    public void ALevelsQuery_NamesEveryKey_SoItReplacesThePreviousLevel()
    {
        var query = new BoulderBrowse(BoulderBrowseLevel.List, HoldType: HoldHandType.Sloper).Query();

        Assert.Equal(new Dictionary<string, object?> { ["browse"] = null, ["style"] = null, ["hold"] = "sloper" }, query);
        Assert.Equal("holds", new BoulderBrowse(BoulderBrowseLevel.HoldTypes).Query()["browse"]);
        Assert.Equal("all", BoulderBrowse.All.Query()["style"]);
    }
}
