// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Client.Views;
using Blocwerk.Contracts.Documents;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientViews.ViewWorld;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>
/// The views' rules apart from the markup: the boulder list's filters and orders (the Server's WallDetail rules), the
/// photo renditions the device holds, the panel grid, and the times.
/// </summary>
public sealed class ViewLogicTests
{
    private static readonly MineMarks Mine = MineMarks.From(Marks());

    private static readonly IReadOnlyList<BouldersDocumentBoulder> Boulders =
    [
        CrimpLine(),
        SlabArchived(),
        DraftBoulder(),
        CrimpLine() with { Id = Guid.NewGuid(), Name = "Hard One", Grade = "V8", CreatedAt = At.AddDays(-2), IsHistoric = true, Stats = new(0, 1, 5) },
        CrimpLine() with { Id = Guid.NewGuid(), Name = "Easy One", Grade = "4", CreatedAt = At.AddDays(-1), Stats = new(0, 0, null) },
    ];

    [Fact]
    public void Newest_First_HistoricSunk_ArchiveApart()
    {
        var (active, archived) = BoulderList.Apply(Boulders, Mine, new BoulderFilter());

        Assert.Equal(["Work in Progress", "Crimp Line", "Easy One", "Hard One"], active.Select(b => b.Name));
        Assert.Equal(["Old Slab"], archived.Select(b => b.Name));
    }

    [Theory]
    [InlineData(BoulderSort.GradeAsc, "Easy One,Crimp Line,Work in Progress,Hard One")]
    [InlineData(BoulderSort.GradeDesc, "Crimp Line,Easy One,Work in Progress,Hard One")]
    [InlineData(BoulderSort.Rating, "Crimp Line,Work in Progress,Easy One,Hard One")]
    [InlineData(BoulderSort.MyAttempts, "Crimp Line,Work in Progress,Easy One,Hard One")]
    [InlineData(BoulderSort.Oldest, "Easy One,Crimp Line,Work in Progress,Hard One")]
    public void Sorts_AsTheServer(BoulderSort sort, string expected)
    {
        var (active, _) = BoulderList.Apply(Boulders, Mine, new BoulderFilter { Sort = sort });

        Assert.Equal(expected.Split(','), active.Select(b => b.Name));
    }

    [Fact]
    public void Filters_ByName_GradeRange_AndTheViewersMarks()
    {
        Assert.Equal(["Crimp Line"], Names(new BoulderFilter { Search = "crimp" }));
        Assert.Equal(["Work in Progress", "Crimp Line"], Names(new BoulderFilter { GradeMin = "5+", GradeMax = "6B" }));
        Assert.Equal(["Crimp Line"], Names(new BoulderFilter { Done = true }));
        Assert.Equal(["Crimp Line"], Names(new BoulderFilter { Attempted = true }));
        Assert.Equal(["Work in Progress", "Easy One", "Hard One"], Names(new BoulderFilter { NotDone = true }));
        Assert.Equal(["Crimp Line"], Names(new BoulderFilter { Favourites = true }));
        Assert.Equal(3, new BoulderFilter { Search = "x", GradeMin = "4", GradeMax = "5", Done = true }.ActiveCount);
    }

    [Fact]
    public void PhotoRenditions_TheDevicesRung_ThenTheThumbnail_ThenTheCap_NeverTheOriginal()
    {
        var sha = PanelSha('1');
        var path = $"/api/walls/{Wall}/a/{sha}";

        Assert.Equal([$"{path}/1920", $"{path}/640"], PhotoUrls.Renditions(Wall, sha, null, 1920));
        Assert.Equal([$"{path}/1920", $"{path}/640"], PhotoUrls.Renditions(Wall, sha, null, 2560));
        Assert.Equal([$"{path}/1280", $"{path}/640", $"{path}/1920"], PhotoUrls.Renditions(Wall, sha, null, 1280));
        Assert.Equal([$"{path}/640?token=a%2Fb", $"{path}/1920?token=a%2Fb"], PhotoUrls.Renditions(Wall, sha, "a/b", 640));
        Assert.Empty(PhotoUrls.Renditions(Wall, null, null, 1920));
        Assert.Empty(PhotoUrls.Renditions(Wall, "ABC", null, 1920));
        Assert.Equal($"{path}/640", PhotoUrls.Thumbnail(Wall, sha, null));
    }

    [Fact]
    public void ThePanelGrid_StartsAtTheOrigin_OrWhereABouldersHoldsAre()
    {
        var grid = new WallPanelGrid(WallDoc(), HoldsDoc());

        Assert.Equal(PanelLeft, grid.First()!.Id);
        Assert.Equal(PanelRight, grid.StartFor(new HashSet<Guid> { HoldRight })!.Id);
        Assert.Equal(PanelLeft, grid.StartFor(new HashSet<Guid> { HoldStart, HoldTop, HoldRight })!.Id);
        Assert.Equal(PanelLeft, grid.StartFor(new HashSet<Guid>())!.Id);
        Assert.Equal(PanelRight, grid.Neighbour(grid.First()!, 1, 0)!.Id);
        Assert.Null(grid.Neighbour(grid.First()!, 0, 1));
        Assert.True(grid.Panels.Single(p => p.Id == PanelRight).Outdated);
        Assert.False(grid.First()!.Outdated);
        Assert.Equal([HoldStart, HoldTop], grid.HoldsOn(grid.First()!).Select(h => h.Id));
    }

    [Fact]
    public void AWallWithoutPanels_IsItsSinglePhoto_WithTheHoldsOfNoPanel()
    {
        var holds = HoldsDoc() with { Holds = [HoldsDoc().Holds[0] with { PanelId = null }, HoldsDoc().Holds[1]] };
        var grid = new WallPanelGrid(WallDoc() with { Panels = [], PhotoSha = PanelSha('9') }, holds);

        var photo = Assert.Single(grid.Panels);
        Assert.Null(photo.Id);
        Assert.Equal(PanelSha('9'), photo.Sha);
        Assert.Equal([HoldStart], grid.HoldsOn(photo).Select(h => h.Id));
        Assert.Null(grid.Neighbour(photo, 1, 0));
        Assert.Empty(new WallPanelGrid(WallDoc() with { Panels = [] }, holds).Panels);
    }

    [Fact]
    public void Times_AsTheServerShowsThem_InLocalTime()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var east = TimeSpan.FromHours(2);

        Assert.Equal("just now", ClientTime.Relative(now.AddSeconds(-10), now, east));
        Assert.Equal("5m ago", ClientTime.Relative(now.AddMinutes(-5), now, east));
        Assert.Equal("3h ago", ClientTime.Relative(now.AddHours(-3), now, east));
        Assert.Equal("yesterday", ClientTime.Relative(now.AddHours(-30), now, east));
        Assert.Equal("4d ago", ClientTime.Relative(now.AddDays(-4), now, east));
        Assert.Equal("Oct 2", ClientTime.Relative(new DateTimeOffset(2026, 10, 1, 23, 0, 0, TimeSpan.Zero), now, east));
        Assert.Equal("Oct 2, 2026", ClientTime.Date(new DateTimeOffset(2026, 10, 1, 23, 0, 0, TimeSpan.Zero), east));
    }

    private static IEnumerable<string> Names(BoulderFilter filter) => BoulderList.Apply(Boulders, Mine, filter).Active.Select(b => b.Name);
}
