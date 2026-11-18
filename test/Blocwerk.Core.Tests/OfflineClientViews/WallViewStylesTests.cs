// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Pages;
using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Tests.OfflineClientStore;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientViews.ViewWorld;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>The offline wall view's style cards, their counts and the list limited to a style.</summary>
public sealed class WallViewStylesTests
{
    private static FakeStoreSource Device(string[] first, string[] second)
    {
        var source = new FakeStoreSource();
        BouldersDocumentBoulder[] boulders =
        [
            CrimpLine() with { Styles = first },
            CrimpLine() with { Id = Guid.NewGuid(), Name = "Second Line", Styles = second },
            SlabArchived(),
        ];
        Put(source, UserA, new()
        {
            [DataDocumentKinds.Wall] = new(Sha('a'), Json(WallDoc())),
            [DataDocumentKinds.Holds] = new(Sha('h'), Json(HoldsDoc())),
            [DataDocumentKinds.Boulders] = new(Sha('b'), Json(new BouldersDocument(1, Wall, boulders))),
            [DataDocumentKinds.Comments] = new(Sha('c'), Json(new CommentsDocument(1, Wall, []))),
        },
        mine: [Marks()]);
        return source;
    }

    private static Task<string> RenderAsync(ViewHarness view) => view.RenderAsync<WallViewPage>(new() { ["WallId"] = Wall });

    [Fact]
    public async Task NoTagsAnywhere_OpensTheListDirectly()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), $"walls/{Wall}");
        var html = await RenderAsync(view);

        Assert.DoesNotContain("style-cards", html, StringComparison.Ordinal);
        Assert.Contains("Crimp Line", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cards_ShowAllAndOnlyTheStylesInUse_WithCounts()
    {
        await using var view = await ViewHarness.StartAsync(Device(["Tension", "Power"], ["Power"]), $"walls/{Wall}");
        var html = await RenderAsync(view);

        Assert.Contains("data-style=\"all\"", html, StringComparison.Ordinal);
        Assert.Contains("data-style=\"tension\"", html, StringComparison.Ordinal);
        Assert.Contains("data-style=\"power\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-style=\"funky\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Crimp Line", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"style-card-count\">2</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStyleInTheUrl_LimitsTheList_AndOffersTheWayBack()
    {
        await using var view = await ViewHarness.StartAsync(Device(["Tension"], ["Power"]), $"walls/{Wall}?style=tension");
        var html = await RenderAsync(view);

        Assert.Contains("Crimp Line", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Second Line", html, StringComparison.Ordinal);
        Assert.Contains("data-style-back", html, StringComparison.Ordinal);
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
