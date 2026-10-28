// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Pages;
using Blocwerk.Client.ThreeD;
using static Blocwerk.Core.Tests.OfflineClient3D.View3DWorld;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientViews.ViewWorld;

namespace Blocwerk.Core.Tests.OfflineClient3D;

/// <summary>
/// The 3D view in PR 3.11's slots: the photo / 3D switch shows only when the device holds the wall's 3D (in full or in
/// part), never with the 3D opt-out; the stage keeps the panel viewer until the switch says 3D, then shows the 3D view
/// (or why not, with the Server page's link) for that page's wall and boulder only.
/// </summary>
public sealed class View3DSlotsTests
{
    [Theory]
    [InlineData("ready", true)]
    [InlineData("missing", true)]
    [InlineData("off", false)]
    [InlineData("none", false)]
    public async Task TheSwitch_ShowsOnlyForA3DModelTheDeviceKnows_AndNeverWithTheOptOut(string state, bool shown)
    {
        var client = new FakeClient3D { Status = new(state, state == "ready" ? DocSha : null, false) };
        await using var view = await StartAsync(Device3D(UserA), $"walls/{Wall}/boulders/{Crimp}", client);

        var wall = await view.RenderAsync<WallViewPage>(new() { ["WallId"] = Wall });
        var boulder = await view.RenderAsync<BoulderPage>(new() { ["WallId"] = Wall, ["BoulderId"] = Crimp });

        Assert.Equal(shown, wall.Contains("bw-client-3d-switch", StringComparison.Ordinal));
        Assert.Equal(shown, boulder.Contains("bw-client-3d-switch", StringComparison.Ordinal));
        Assert.All(client.StatusReads, read => Assert.Equal((UserA, Wall), read));
    }

    [Fact]
    public async Task OnThePhotos_TheStageIsThePanelViewer_AndNo3DViewMounts()
    {
        var client = new FakeClient3D { Status = Ready };
        await using var view = await StartAsync(Device3D(UserA), $"walls/{Wall}", client);

        var html = await view.RenderAsync<WallViewPage>(new() { ["WallId"] = Wall });

        Assert.Contains("class=\"panel-viewer\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("bw-client-3d\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-pressed=\"false\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On3D_TheWallsStageIsThe3DView_WithTheFullPagesLink()
    {
        var toggle = new View3DToggle();
        toggle.Set(Wall, null, true);
        await using var view = await StartAsync(Device3D(UserA), $"walls/{Wall}", new FakeClient3D { Status = Ready }, toggle);

        var html = await view.RenderAsync<WallViewPage>(new() { ["WallId"] = Wall });

        Assert.Contains("data-bw-3d=\"ready\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"wall3d-stage-el\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"panel-viewer\"", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"walls/{Wall}/3d\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-pressed=\"true\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On3D_ABouldersStageHighlightsIt_AndAnotherBoulderStaysOnThePhotos()
    {
        var toggle = new View3DToggle();
        toggle.Set(Wall, Crimp, true);
        await using var view = await StartAsync(Device3D(UserA), $"walls/{Wall}/boulders/{Crimp}", new FakeClient3D { Status = Ready }, toggle);

        var crimp = await view.RenderAsync<BoulderPage>(new() { ["WallId"] = Wall, ["BoulderId"] = Crimp });
        Assert.Contains("class=\"wall3d-stage-el\"", crimp, StringComparison.Ordinal);
        Assert.Contains("wall3d-inline-legend", crimp, StringComparison.Ordinal);
        Assert.Contains($"href=\"walls/{Wall}/3d?boulder={Crimp}\"", crimp, StringComparison.Ordinal);

        await using var other = await StartAsync(Device3D(UserA), $"walls/{Wall}/boulders/{Draft}", new FakeClient3D { Status = Ready }, toggle);
        var draft = await other.RenderAsync<BoulderPage>(new() { ["WallId"] = Wall, ["BoulderId"] = Draft });
        Assert.Contains("class=\"panel-viewer\"", draft, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing", "not saved on this device")]
    [InlineData("off", "3D is turned off on this device")]
    public async Task A3DViewTheDeviceCannotShow_SaysWhy_WithTheServerPage(string state, string text)
    {
        var toggle = new View3DToggle();
        toggle.Set(Wall, null, true);
        await using var view = await StartAsync(Device3D(Anon), $"walls/{Wall}/shared/tok", new FakeClient3D { Status = new(state, null, false) }, toggle);

        var html = await view.RenderAsync<WallViewPage>(new() { ["WallId"] = Wall, ["ShareToken"] = "tok" });

        Assert.Contains(text, html, StringComparison.Ordinal);
        Assert.Contains($"href=\"/walls/{Wall}/shared/tok/3d\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("wall3d-stage-el", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavingThePage_PutsItsSwitchBackOnThePhotos()
    {
        var toggle = new View3DToggle();
        toggle.Set(Wall, null, true);
        var view = await StartAsync(Device3D(UserA), $"walls/{Wall}", new FakeClient3D { Status = Ready }, toggle);
        await view.RenderAsync<WallViewPage>(new() { ["WallId"] = Wall });

        await view.DisposeAsync();

        Assert.False(toggle.IsShown(Wall, null));
    }

    [Fact]
    public void TheToggle_HoldsOnePage_AndSaysWhenItFlips()
    {
        var toggle = new View3DToggle();
        var flips = 0;
        toggle.Changed += () => flips++;

        toggle.Set(Wall, Crimp, true);
        toggle.Set(Wall, Crimp, true);
        Assert.True(toggle.IsShown(Wall, Crimp));
        Assert.False(toggle.IsShown(Wall, null));
        toggle.Set(Wall, null, false);
        Assert.True(toggle.IsShown(Wall, Crimp), "another page's off leaves it");
        toggle.Set(Wall, Crimp, false);

        Assert.False(toggle.IsShown(Wall, Crimp));
        Assert.Equal(2, flips);
    }
}
