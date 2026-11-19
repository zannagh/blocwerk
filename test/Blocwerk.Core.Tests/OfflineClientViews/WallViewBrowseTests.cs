// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Pages;
using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Tests.OfflineClientStore;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientViews.ViewWorld;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>The offline wall view's boulder browser: the landing, the card pages with their counts, and the limited lists.</summary>
public sealed class WallViewBrowseTests
{
    // The second boulder, the viewer's one project (Crimp Line is the favourite).
    private static readonly Guid Second = Guid.Parse("5ec0bd00-0000-4000-8000-000000000002");

    private static FakeStoreSource Device(string[] first, string[] second, string? secondHoldType = null, string? startHandType = null)
    {
        var source = new FakeStoreSource();
        BouldersDocumentBoulder[] boulders =
        [
            CrimpLine() with { Styles = first },
            CrimpLine() with { Id = Second, Name = "Second Line", Styles = second, HoldType = secondHoldType },
            SlabArchived(),
        ];
        var holds = HoldsDoc();
        holds = holds with { Holds = holds.Holds.Select(h => h.Id == HoldStart ? h with { HandType = startHandType } : h).ToList() };
        Put(source, UserA, new()
        {
            [DataDocumentKinds.Wall] = new(Sha('a'), Json(WallDoc())),
            [DataDocumentKinds.Holds] = new(Sha('h'), Json(holds)),
            [DataDocumentKinds.Boulders] = new(Sha('b'), Json(new BouldersDocument(1, Wall, boulders))),
            [DataDocumentKinds.Comments] = new(Sha('c'), Json(new CommentsDocument(1, Wall, []))),
        },
        mine: [Marks() with { Projects = [Second] }]);
        return source;
    }

    private static Task<string> RenderAsync(ViewHarness view) => view.RenderAsync<WallViewPage>(new() { ["WallId"] = Wall });

    private static Task<string> RenderBoulderAsync(ViewHarness view, Guid id) =>
        view.RenderAsync<BoulderPage>(new() { ["WallId"] = Wall, ["BoulderId"] = id });

    [Fact]
    public async Task NoTagsAnywhere_StillOpensOnTheLanding_WithTheEmptyCardsGreyedOut()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), $"walls/{Wall}");
        var html = await RenderAsync(view);

        Assert.Matches("<a class=\"style-card browse-hero is-link\" href=\"[^\"]*\\?style=all\" data-card=\"all\"", html);
        Assert.Contains("<a class=\"style-card browse-tall is-empty\" aria-disabled=\"true\" data-card=\"holds\">", html, StringComparison.Ordinal);
        Assert.Contains("<a class=\"style-card browse-tall is-empty\" aria-disabled=\"true\" data-card=\"styles\">", html, StringComparison.Ordinal);
        // The landing keeps the wall's photo (and the stage the 3D switch uses) and its counts.
        Assert.Contains("wall-photo-container", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLanding_CarriesTheWholeListUnderItsCards_ForTheDesktopTier()
    {
        await using var view = await ViewHarness.StartAsync(Device(["Tension"], ["Power"]), $"walls/{Wall}");
        var html = await RenderAsync(view);

        Assert.Contains("data-browse-level=\"landing\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"boulder-list-block browse-landing-list\" data-browse-list=\"landing\"", html, StringComparison.Ordinal);
        Assert.Contains("browse-list-divider", html, StringComparison.Ordinal);
        Assert.Contains("Crimp Line", html, StringComparison.Ordinal);
        Assert.Contains("Second Line", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"walls/{Wall}/boulders/{Crimp}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedIn_TheLandingHasAFavoritesCard_CountingTheFavouriteFamilies()
    {
        await using var view = await ViewHarness.StartAsync(Device([], []), $"walls/{Wall}");
        var html = await RenderAsync(view);

        Assert.Matches("<a class=\"style-card browse-mine is-link\" href=\"[^\"]*\\?mine=favorites\" data-card=\"favorites\">", html);
        Assert.Matches("(?s)data-card=\"favorites\">.*?<span class=\"style-card-count\">1</span>", html);
    }

    [Fact]
    public async Task SignedIn_TheLandingHasAMyProjectsCard_AndItsListHoldsTheProjects()
    {
        await using var landing = await ViewHarness.StartAsync(Device([], []), $"walls/{Wall}");
        var html = await RenderAsync(landing);
        Assert.Matches("<a class=\"style-card browse-mine is-link\" href=\"[^\"]*\\?mine=projects\" data-card=\"projects\">", html);
        Assert.Matches("(?s)data-card=\"projects\">.*?<span class=\"style-card-count\">1</span>", html);

        await using var list = await ViewHarness.StartAsync(Device([], []), $"walls/{Wall}?mine=projects");
        var rows = await RenderAsync(list);
        Assert.Contains("Second Line", rows, StringComparison.Ordinal);
        Assert.DoesNotContain("Crimp Line", rows, StringComparison.Ordinal);
        Assert.Contains($"href=\"walls/{Wall}/boulders/{Second}?mine=projects\"", rows, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Kiosk)]
    [InlineData(Anon)]
    public async Task WithoutMarks_TheLandingHasNoOwnListCards_AndTheListIsEmpty(string partition)
    {
        await using var landing = await ViewHarness.StartAsync(CachedDevice(partition), $"walls/{Wall}");
        var html = await RenderAsync(landing);
        Assert.DoesNotContain("data-card=\"favorites\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-card=\"projects\"", html, StringComparison.Ordinal);

        await using var list = await ViewHarness.StartAsync(CachedDevice(partition), $"walls/{Wall}?mine=favorites");
        Assert.DoesNotContain("Crimp Line", await RenderAsync(list), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MineFavorites_LimitsTheListToTheFavourites_AndTheBoulderLinkReturnsToIt()
    {
        await using var view = await ViewHarness.StartAsync(Device([], []), $"walls/{Wall}?mine=favorites");
        var html = await RenderAsync(view);

        Assert.Contains("Crimp Line", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Second Line", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"walls/{Wall}/boulders/{Crimp}?mine=favorites\"", html, StringComparison.Ordinal);
        Assert.Contains("data-browse-back", html, StringComparison.Ordinal);
        Assert.Contains("data-browse-list=\"level\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBoulderPage_OpenedFromTheFavourites_BacksIntoThem()
    {
        await using var view = await ViewHarness.StartAsync(Device([], []), $"walls/{Wall}/boulders/{Crimp}?mine=favorites");
        var html = await RenderBoulderAsync(view, Crimp);

        Assert.Contains($"href=\"walls/{Wall}?mine=favorites\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStylesPage_ShowsEveryStyle_CountsFamilies_AndGreysOutTheEmptyOnes()
    {
        await using var view = await ViewHarness.StartAsync(Device(["Tension", "Power"], ["Power"]), $"walls/{Wall}?browse=styles");
        var html = await RenderAsync(view);

        Assert.Contains("data-card=\"warmup\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-disabled=\"true\" data-card=\"funky\"", html, StringComparison.Ordinal);
        Assert.Matches("href=\"[^\"]*\\?style=power\" data-card=\"power\"", html);
        Assert.Contains("<span class=\"style-card-count\">2</span>", html, StringComparison.Ordinal);
        Assert.Contains("data-browse-back", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCards_ReplaceThePhotoAndCounts_SoTheyFitOneScreen()
    {
        await using var view = await ViewHarness.StartAsync(Device(["Tension"], ["Power"]), $"walls/{Wall}?browse=holds");
        var html = await RenderAsync(view);

        Assert.Contains("data-browse-level=\"holdtypes\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("wall-photo-container", html, StringComparison.Ordinal);
        Assert.DoesNotContain("stats-bar", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHoldTypeSetOnABoulder_LimitsTheList_AndItsRowCarriesIt()
    {
        await using var view = await ViewHarness.StartAsync(Device([], [], secondHoldType: "Sloper"), $"walls/{Wall}?hold=sloper");
        var html = await RenderAsync(view);

        Assert.Contains("Second Line", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Crimp Line", html, StringComparison.Ordinal);
        Assert.Contains("?hold=sloper\"", html, StringComparison.Ordinal);
        Assert.Contains("wall-photo-container", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHoldsDocument_InfersTheType_WhenHalfTheGrippedHoldsHaveOne()
    {
        await using var view = await ViewHarness.StartAsync(Device([], [], startHandType: "Crimp"), $"walls/{Wall}?hold=crimp");
        var html = await RenderAsync(view);

        Assert.Contains("Crimp Line", html, StringComparison.Ordinal);
        Assert.Contains("Second Line", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBoulderPage_ShowsAnInferredTypeAsSuch_AndBacksIntoItsList()
    {
        await using var view = await ViewHarness.StartAsync(Device([], [], startHandType: "Crimp"), $"walls/{Wall}/boulders/{Crimp}?hold=crimp");
        var html = await RenderBoulderAsync(view, Crimp);

        Assert.Contains("data-hold-type=\"crimp\" data-inferred=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("from holds", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"walls/{Wall}?hold=crimp\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStyleInTheUrl_LimitsTheList_AndOffersTheWayBack()
    {
        await using var view = await ViewHarness.StartAsync(Device(["Tension"], ["Power"]), $"walls/{Wall}?style=tension");
        var html = await RenderAsync(view);

        Assert.Contains("Crimp Line", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Second Line", html, StringComparison.Ordinal);
        Assert.Contains("data-browse-back", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"walls/{Wall}/boulders/{Crimp}?style=tension\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StyleAll_ShowsTheWholeList()
    {
        await using var view = await ViewHarness.StartAsync(Device(["Tension"], ["Power"]), $"walls/{Wall}?style=all");
        var html = await RenderAsync(view);

        Assert.Contains("Crimp Line", html, StringComparison.Ordinal);
        Assert.Contains("Second Line", html, StringComparison.Ordinal);
    }
}
