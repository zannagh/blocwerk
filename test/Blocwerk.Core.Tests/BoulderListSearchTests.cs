// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Pages.Walls;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The boulder list's search matches what a row shows (name, grade, setters), ignoring case and accents, and needs every
/// word of the query to match somewhere.
/// </summary>
public class BoulderListSearchTests
{
    private static BoulderListItem Item(string name, string? grade = null, params string[] setters) =>
        new(new Boulder { Name = name, Grade = grade }, 0, false, false, false, null, 0, null, setters.Length > 0 ? setters : null);

    private static bool Finds(string query, BoulderListItem item) =>
        BoulderListSearch.Matches(item, BoulderListSearch.Terms(query));

    [Theory]
    [InlineData("crimp")]
    [InlineData("CRIMP")]
    [InlineData("Crimp Line")]
    [InlineData("line crimp")]
    public void MatchesTheNameIgnoringCaseAndWordOrder(string query)
    {
        Assert.True(Finds(query, Item("Crimp line")));
    }

    [Theory]
    [InlineData("eclair", "Éclair")]
    [InlineData("ÉCLAIR", "eclair")]
    [InlineData("muller", "Müller's traverse")]
    public void IgnoresAccents(string query, string name)
    {
        Assert.True(Finds(query, Item(name)));
    }

    [Fact]
    public void MatchesTheGradeAndTheSetter()
    {
        var item = Item("Crimp line", "6A+", "Zoë");

        Assert.True(Finds("6a+", item));
        Assert.True(Finds("zoe", item));
        Assert.True(Finds("6a crimp zoe", item));
    }

    [Fact]
    public void EveryWordHasToMatch()
    {
        Assert.False(Finds("crimp sloper", Item("Crimp line", "6A+")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyQueryMatchesEverything(string? query)
    {
        Assert.Empty(BoulderListSearch.Terms(query));
        Assert.True(BoulderListSearch.Matches(Item("Anything"), BoulderListSearch.Terms(query)));
    }
}
