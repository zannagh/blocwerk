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
    private static FakeStoreSource Device(string[] first, string[] second, string? secondHoldType = null, string? startHandType = null)
    {
        var source = new FakeStoreSource();
        BouldersDocumentBoulder[] boulders =
        [
            CrimpLine() with { Styles = first },
            CrimpLine() with { Id = Guid.NewGuid(), Name = "Second Line", Styles = second, HoldType = secondHoldType },
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
        mine: [Marks()]);
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

        Assert.Contains("<a class=\"style-card style-card-all\" href=", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"style-card is-empty style-card-all\" aria-disabled=\"true\" data-card=\"holds\">", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"style-card is-empty style-card-all\" aria-disabled=\"true\" data-card=\"styles\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Crimp Line", html, StringComparison.Ordinal);
        // The landing keeps the wall's photo (and the stage the 3D switch uses) and its counts.
        Assert.Contains("wall-photo-container", html, StringComparison.Ordinal);
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
