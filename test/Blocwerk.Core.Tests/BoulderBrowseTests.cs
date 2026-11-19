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

        Assert.Equal(new Dictionary<string, object?> { ["browse"] = null, ["style"] = null, ["hold"] = "sloper", ["mine"] = null }, query);
        Assert.Equal("holds", new BoulderBrowse(BoulderBrowseLevel.HoldTypes).Query()["browse"]);
        Assert.Equal("all", BoulderBrowse.All.Query()["style"]);
    }

    [Theory]
    [InlineData("favorites")]
    [InlineData("Favourites")]
    public void MineFavorites_IsAListOfItsOwn_UnderTheLanding(string value)
    {
        var browse = BoulderBrowse.From(null, null, null, value);

        Assert.Equal(new BoulderBrowse(BoulderBrowseLevel.List, Mine: BoulderMine.Favorites), browse);
        Assert.False(browse.IsAll);
        Assert.Equal(BoulderBrowseLevel.Landing, browse.Up.Level);
        Assert.Equal("Boulders", browse.UpLabel);
        Assert.Equal("mine=favorites", browse.ListQuery);
    }

    [Fact]
    public void MineFavorites_Query_ClearsTheOtherLevels_AndALevelClearsIt()
    {
        var mine = new BoulderBrowse(BoulderBrowseLevel.List, Mine: BoulderMine.Favorites).Query();

        Assert.Equal(new Dictionary<string, object?> { ["browse"] = null, ["style"] = null, ["hold"] = null, ["mine"] = "favorites" }, mine);
        Assert.Null(BoulderBrowse.All.Query()["mine"]);
        Assert.Null(BoulderBrowse.Landing.Query()["mine"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("projects-of-someone")]
    public void AnUnknownMineValue_FallsBackToTheLanding(string? value) =>
        Assert.Equal(BoulderBrowse.Landing, BoulderBrowse.From(null, null, null, value));

    [Fact]
    public void MineProjects_IsAListOfItsOwn_WithItsOwnQuery()
    {
        var browse = BoulderBrowse.From(null, null, null, "PROJECTS");

        Assert.Equal(new BoulderBrowse(BoulderBrowseLevel.List, Mine: BoulderMine.Projects), browse);
        Assert.Equal(BoulderBrowseLevel.Landing, browse.Up.Level);
        Assert.Equal("mine=projects", browse.ListQuery);
        Assert.Equal("projects", browse.Query()["mine"]);
        Assert.Null(browse.Query()["style"]);
    }

    [Fact]
    public void AHoldTypeInTheUrl_WinsOverMine() =>
        Assert.Equal(HoldHandType.Crimp, BoulderBrowse.From(null, null, "crimp", "favorites").HoldType);
}
