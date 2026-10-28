// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Pages;
using Blocwerk.Client.Views;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientViews.ViewWorld;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>
/// The views' extension points for PR 3.13 (gallery and 3D): empty, a page renders exactly its own content; a mounted
/// component gets the cached wall, the boulder and the token, and on a stage slot the panel viewer to keep or replace.
/// </summary>
public sealed class ViewSlotTests
{
    [Fact]
    public async Task EmptySlots_RenderNothingOfTheirOwn()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), $"walls/{Wall}/boulders/{Crimp}");
        var html = await view.RenderAsync<BoulderPage>(new() { ["WallId"] = Wall, ["BoulderId"] = Crimp });

        Assert.DoesNotContain("bw-view-slot", html, StringComparison.Ordinal);
        Assert.Contains("class=\"panel-viewer\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMountedComponent_GetsTheContext_AndTheStagesDefaultContent()
    {
        var slots = new ViewSlotRegistry()
            .Add<ProbeSlotComponent>(ViewSlotName.BoulderStage)
            .Add<ProbeSlotComponent>(ViewSlotName.BoulderActions);
        await using var view = await ViewHarness.StartAsync(CachedDevice(Anon), $"walls/{Wall}/boulders/{Crimp}/shared/tok", slots);
        var html = await view.RenderAsync<BoulderPage>(new() { ["WallId"] = Wall, ["BoulderId"] = Crimp, ["ShareToken"] = "tok" });

        Assert.Contains("data-bw-slot=\"BoulderStage\"", html, StringComparison.Ordinal);
        Assert.Contains("data-bw-slot=\"BoulderActions\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-wall=\"{Wall}\" data-boulder=\"Crimp Line\" data-token=\"tok\"", html, StringComparison.Ordinal);
        var stage = html.IndexOf("data-bw-slot=\"BoulderStage\"", StringComparison.Ordinal);
        Assert.True(html.IndexOf("class=\"panel-viewer\"", stage, StringComparison.Ordinal) > stage, "the stage's component holds the panel viewer");
    }

    [Fact]
    public async Task TheWallViewsSlots_AreThere()
    {
        var slots = new ViewSlotRegistry()
            .Add<ProbeSlotComponent>(ViewSlotName.WallActions)
            .Add<ProbeSlotComponent>(ViewSlotName.WallStage)
            .Add<ProbeSlotComponent>(ViewSlotName.WallBelow);
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), $"walls/{Wall}", slots);
        var html = await view.RenderAsync<WallViewPage>(new() { ["WallId"] = Wall });

        Assert.Equal(3, html.Split("class=\"probe\"").Length - 1);
        Assert.Contains("data-boulder=\"none\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"panel-viewer\"", html, StringComparison.Ordinal);
    }
}
