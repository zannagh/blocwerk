// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Web.Components.Pages.Walls;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The boulder list's filter cookie is written by the server and read back on the next visit, through JS that treats it
/// as opaque. A wall's filters must come back exactly, other walls' must survive a write, the search must never be
/// stored, and an old or tampered-with value must degrade to "no filters" rather than throw.
/// </summary>
public class BoulderListFilterCookieTests
{
    private static readonly Guid WallA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WallB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static WallListFilter Filter(string? min = null, string? max = null, bool done = false, int sort = 0, string search = "") =>
        new(search, min, max, Attempted: false, Done: done, NotDone: false, Favorites: false, Created: false, Beta: false, NeedsReview: false, sort);

    [Fact]
    public void RoundTripsEveryField()
    {
        var all = new WallListFilter(string.Empty, "6A+", "7B", true, true, true, true, true, true, true, 4);

        var cookie = BoulderListFilterCookie.Write(null, WallA, all);

        Assert.Equal(all, BoulderListFilterCookie.Read(cookie, WallA));
    }

    [Fact]
    public void NeverStoresTheSearch()
    {
        var cookie = BoulderListFilterCookie.Write(null, WallA, Filter(done: true, search: "crimp line"));

        Assert.DoesNotContain("crimp", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, BoulderListFilterCookie.Read(cookie, WallA)!.SearchText);
    }

    [Fact]
    public void KeepsOtherWallsAndPutsTheLatestFirst()
    {
        var cookie = BoulderListFilterCookie.Write(null, WallA, Filter(done: true));
        cookie = BoulderListFilterCookie.Write(cookie, WallB, Filter(min: "V3"));

        Assert.StartsWith(WallB.ToString("N"), cookie, StringComparison.Ordinal);
        Assert.True(BoulderListFilterCookie.Read(cookie, WallA)!.Done);
        Assert.Equal("V3", BoulderListFilterCookie.Read(cookie, WallB)!.GradeMin);
    }

    [Fact]
    public void DefaultFiltersRemoveTheWallInsteadOfStoringIt()
    {
        var cookie = BoulderListFilterCookie.Write(null, WallA, Filter(done: true));

        cookie = BoulderListFilterCookie.Write(cookie, WallA, Filter());

        Assert.Equal(string.Empty, cookie);
        Assert.Null(BoulderListFilterCookie.Read(cookie, WallA));
    }

    [Fact]
    public void RemembersAtMostTwelveWalls()
    {
        string? cookie = null;
        var walls = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
        foreach (var wall in walls)
        {
            cookie = BoulderListFilterCookie.Write(cookie, wall, Filter(min: "6A+", max: "8C+", done: true, sort: 3));
        }

        Assert.Equal(BoulderListFilterCookie.MaxWalls, cookie!.Split('|').Length);
        Assert.NotNull(BoulderListFilterCookie.Read(cookie, walls[^1]));
        Assert.Null(BoulderListFilterCookie.Read(cookie, walls[0]));
        Assert.True(cookie.Length < 1000, $"cookie is {cookie.Length} bytes");
    }

    [Fact]
    public void GradesWithSeparatorsSurvive()
    {
        var cookie = BoulderListFilterCookie.Write(null, WallA, Filter(min: "5.12a", max: "a|b,c"));

        var back = BoulderListFilterCookie.Read(cookie, WallA)!;

        Assert.Equal("5.12a", back.GradeMin);
        Assert.Equal("a|b,c", back.GradeMax);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("11111111111111111111111111111111,x,0,,")]
    [InlineData("11111111111111111111111111111111,1,0,,,extra")]
    [InlineData("11111111111111111111111111111111,-1,0,,")]
    [InlineData("11111111111111111111111111111111,1,999,,")]
    [InlineData("11111111111111111111111111111111,1,0,AVeryLongGradeValueThatIsNotAGrade,")]
    public void MalformedValuesReadAsNoFilters(string? stored)
    {
        Assert.Null(BoulderListFilterCookie.Read(stored, WallA));
    }

    [Fact]
    public void AMalformedEntryDoesNotHideTheGoodOnes()
    {
        var good = BoulderListFilterCookie.Write(null, WallA, Filter(done: true));

        Assert.True(BoulderListFilterCookie.Read("junk|" + good, WallA)!.Done);
    }
}
