// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Layout;
using Blocwerk.Client.Pages;
using Blocwerk.Client.Store;
using Blocwerk.Client.Views;
using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Tests.OfflineClientStore;
using Microsoft.AspNetCore.Components;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientViews.ViewWorld;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>
/// What keeps the views standing and in their lane: a view that throws is contained with the way online and Clear;
/// names a document left empty break nothing; an outsider's or kiosk's class never shows a draft or an archived boulder
/// even if one arrived; "My Walls" lists only the viewer's own walls; only the open wall stays parsed; a stage slot
/// takes one component.
/// </summary>
public sealed class ViewRobustnessTests
{
    [Fact]
    public async Task AViewThatThrows_ShowsTheError_WithTheWayOnline_AndClearOfflineData()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), $"walls/{Wall}?x=1");
        RenderFragment broken = b =>
        {
            b.OpenComponent<ThrowingComponent>(0);
            b.CloseComponent();
        };
        var html = await view.RenderAsync<ViewErrorBoundary>(new() { ["ChildContent"] = broken });

        Assert.Contains("Couldn't show this page", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"/walls/{Wall}?x=1\"", html, StringComparison.Ordinal);
        Assert.Contains("Clear offline data", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamesTheDocumentLeftEmpty_BreakNothing()
    {
        var nameless = new FeedActor(null, null!, false, null);
        var device = CachedDevice(UserA, [CommentBy(CommentA, nameless, "who wrote this", 0), CommentBy(CommentB, Alice with { Name = string.Empty }, "and this", 1)]);
        await using var view = await ViewHarness.StartAsync(device, $"walls/{Wall}/boulders/{Crimp}");
        var html = await view.RenderAsync<BoulderPage>(new() { ["WallId"] = Wall, ["BoulderId"] = Crimp });

        Assert.Contains("who wrote this", html, StringComparison.Ordinal);
        Assert.Contains("and this", html, StringComparison.Ordinal);
        Assert.Equal(2, html.Split("<div class=\"avatar avatar-sm\" aria-hidden=\"true\"></div>").Length - 1);
        Assert.DoesNotContain("\0", html, StringComparison.Ordinal);

        var unnamed = CrimpLine() with { Name = null! };
        Assert.Empty(BoulderList.Apply([unnamed], MineMarks.None, new BoulderFilter { Search = "crimp" }).Active);
    }

    [Theory]
    [InlineData(Anon)]
    [InlineData(Kiosk)]
    public async Task AnOutsidersClass_NeverShowsADraftOrAnArchivedBoulder_EvenIfOneArrived(string partition)
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(partition), $"walls/{Wall}");
        var token = partition == Anon ? "tok" : null;
        var wall = await view.RenderAsync<WallViewPage>(new() { ["WallId"] = Wall, ["ShareToken"] = token });
        var draft = await view.RenderAsync<BoulderPage>(new() { ["WallId"] = Wall, ["BoulderId"] = Draft, ["ShareToken"] = token });

        Assert.Contains("Crimp Line", wall, StringComparison.Ordinal);
        Assert.DoesNotContain("Work in Progress", wall, StringComparison.Ordinal);
        Assert.DoesNotContain("Old Slab", wall, StringComparison.Ordinal);
        Assert.DoesNotContain("Archive (", wall, StringComparison.Ordinal);
        Assert.Contains("This boulder is not available offline", draft, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MyWalls_ListsTheViewersOwnWalls_NeverOneCachedThroughAShareLink()
    {
        var shared = new FakeStoreSource();
        Put(shared, UserA, CachedDevice(UserA).Docs[(UserA, Wall)].Docs.ToDictionary(), member: false);
        await using var outsider = await ViewHarness.StartAsync(shared, "walls");
        Assert.Contains("No walls saved on this device yet.", await outsider.RenderAsync<WallListPage>([]), StringComparison.Ordinal);

        await using var kiosk = await ViewHarness.StartAsync(CachedDevice(Kiosk), "walls");
        var tablet = await kiosk.RenderAsync<WallListPage>([]);
        Assert.Contains("<h3>The Attic</h3>", tablet, StringComparison.Ordinal);
        Assert.Contains("1 boulder<", tablet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWallList_ParsesNoWallsHoldsOrComments_AndKeepsOnlyTheOpenWallParsed()
    {
        var source = CachedDevice(UserA);
        source.Docs[(UserA, Wall)] = source.Docs[(UserA, Wall)] with
        {
            Docs = new Dictionary<string, StoreDocText>(source.Docs[(UserA, Wall)].Docs) { [DataDocumentKinds.Holds] = new(Sha('h'), "{ not json") },
        };
        var summary = WallSummaries.Summarise(source.Docs[(UserA, Wall)], StoreAudience.Member);
        Assert.Equal(new WallSummary(Wall, "The Attic", PanelSha('1'), 2), summary);

        var store = new ClientStore(CachedDevice(UserA));
        await store.StartAsync();
        var first = await store.GetWallAsync(Wall);
        Assert.Same(first, await store.GetWallAsync(Wall));
    }

    [Fact]
    public void AStageSlot_TakesOneComponent()
    {
        var slots = new ViewSlotRegistry().Add<ProbeSlotComponent>(ViewSlotName.BoulderStage).Add<ProbeSlotComponent>(ViewSlotName.WallBelow);

        Assert.Throws<InvalidOperationException>(() => slots.Add<ProbeSlotComponent>(ViewSlotName.BoulderStage));
        slots.Add<ProbeSlotComponent>(ViewSlotName.WallBelow);
        Assert.Equal(2, slots.For(ViewSlotName.WallBelow).Count);
    }
}
