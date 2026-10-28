// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Pages;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientViews.ViewWorld;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>
/// A boulder's holds of no panel (never-stamped virtual holds, legacy ones) are drawn on the centre panel, as the
/// Server's BoulderDetail places them (<c>RenderPanelId</c>), and the boulder page draws the boulder's holds alone.
/// </summary>
public sealed class VirtualHoldTests
{
    // The holds as the overlay draws them: the virtual top at (0.7, 0.3), the second virtual hold at (0.6, 0.6), the
    // right panel's start at (0.5, 0.5), and the left panel's start hold of another boulder at (0.2, 0.8).
    private const string VirtualTopHold = "cx=\"70.00\" cy=\"30.00\"";
    private const string SecondVirtual = "cx=\"60.00\" cy=\"60.00\"";

    [Fact]
    public async Task AVirtualTop_IsDrawnOnTheCentrePanel_AndTheChevronsReachBothPanels()
    {
        await using var view = await ViewHarness.StartAsync(VirtualDevice(UserA), $"walls/{Wall}/boulders/{VirtualTop}");
        var html = await RenderAsync(view, VirtualTop);

        // One hold on each panel: the tie goes to the origin, where the virtual top is drawn, dashed as on the Server.
        Assert.Contains($"data-bw-panel=\"{PanelLeft}\"", html, StringComparison.Ordinal);
        Assert.Contains(VirtualTopHold, html, StringComparison.Ordinal);
        Assert.Contains("panel-arrow right", html, StringComparison.Ordinal);
        Assert.Contains("1 top", html, StringComparison.Ordinal);
        Assert.DoesNotContain("not on the wall", html, StringComparison.Ordinal);

        // Only the boulder's own holds: not the left panel's other holds.
        Assert.DoesNotContain(WallViewPageTests.StartHold, html, StringComparison.Ordinal);
        Assert.DoesNotContain("cx=\"50.00\" cy=\"10.00\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABoulderOfVirtualHoldsOnly_IsDrawnOnTheCentrePanel_NotABarePhoto()
    {
        await using var view = await ViewHarness.StartAsync(VirtualDevice(UserA), $"walls/{Wall}/boulders/{AllVirtual}");
        var html = await RenderAsync(view, AllVirtual);

        Assert.Contains($"data-bw-panel=\"{PanelLeft}\"", html, StringComparison.Ordinal);
        Assert.Contains(VirtualTopHold, html, StringComparison.Ordinal);
        Assert.Contains(SecondVirtual, html, StringComparison.Ordinal);
        Assert.DoesNotContain("panel-arrow", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGrid_PlacesABouldersHoldsOfNoPanel_OnTheCentre_AndTheWallViewsNowhere()
    {
        var holds = HoldsDoc() with { Holds = [.. HoldsDoc().Holds, HoldsDoc().Holds[0] with { Id = HoldVirtual, PanelId = null, Virtual = true }] };
        var grid = new Blocwerk.Client.Views.WallPanelGrid(WallDoc(), holds);
        var ids = new HashSet<Guid> { HoldVirtual, HoldRight };

        Assert.Equal(PanelLeft, grid.Centre!.Id);
        Assert.DoesNotContain(grid.HoldsOn(grid.Centre), h => h.Id == HoldVirtual);
        Assert.Equal([HoldVirtual], grid.BoulderHoldsOn(grid.Centre, ids).Select(h => h.Id));
        Assert.Equal([HoldRight], grid.BoulderHoldsOn(grid.Panels.Single(p => p.Id == PanelRight), ids).Select(h => h.Id));
        Assert.Equal(new HashSet<Guid> { PanelLeft, PanelRight }, grid.PanelsOf(ids));
    }

    private static Task<string> RenderAsync(ViewHarness view, Guid boulder) =>
        view.RenderAsync<BoulderPage>(new() { ["WallId"] = Wall, ["BoulderId"] = boulder });
}
