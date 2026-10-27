// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Pages;
using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientViews.ViewWorld;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>
/// The offline wall view (PR 3.11): the wall's photos with the shared overlay, the counts, and the boulder list with the
/// viewer's marks, the archive apart and drafts only as the class's documents carry them.
/// </summary>
public sealed class WallViewPageTests
{
    // A hold as the shared overlay draws it (no ids on the read-only overlay): the left panel's start hold at (0.2, 0.8).
    internal const string StartHold = "cx=\"20.00\" cy=\"80.00\"";

    [Fact]
    public async Task ShowsTheWall_ItsFirstPanel_AndTheBoulderList()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), $"walls/{Wall}");
        var html = await RenderAsync(view);

        Assert.Contains("<h1 title=\"The Attic\">The Attic</h1>", html, StringComparison.Ordinal);
        Assert.Contains("Steep and short", html, StringComparison.Ordinal);
        Assert.Contains($"data-bw-panel=\"{PanelLeft}\"", html, StringComparison.Ordinal);
        Assert.Contains($"src=\"/api/walls/{Wall}/a/{PanelSha('1')}/1920\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"hold-overlay\"", html, StringComparison.Ordinal);
        // As on the Server's wall view, no hold is drawn until "Show all holds".
        Assert.DoesNotContain(StartHold, html, StringComparison.Ordinal);
        Assert.Contains("holds-all-tool", html, StringComparison.Ordinal);
        Assert.Contains("panel-arrow right", html, StringComparison.Ordinal);
        Assert.DoesNotContain("panel-arrow left", html, StringComparison.Ordinal);

        // 3 holds; 2 active boulders (the draft is the members' class's), the archive apart.
        Assert.Contains("<span class=\"stat-value\">3</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"stat-value\">2</span>", html, StringComparison.Ordinal);
        Assert.Contains("Archive (1)", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"walls/{Wall}/boulders/{Crimp}\"", html, StringComparison.Ordinal);
        Assert.Contains("draft-badge", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("Work in Progress", StringComparison.Ordinal) < html.IndexOf("Crimp Line", StringComparison.Ordinal), "newest first");
    }

    [Fact]
    public async Task TheViewersMarks_ShowOnTheRows_AndTheirFiltersAreOffered()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), $"walls/{Wall}");
        var html = await RenderAsync(view);

        var crimp = Row(html, Crimp);
        Assert.Contains("fav-icon", crimp, StringComparison.Ordinal);
        Assert.Contains("send-icon", crimp, StringComparison.Ordinal);
        Assert.Contains("flash-icon", Row(html, Slab), StringComparison.Ordinal);
        Assert.DoesNotContain("status-icon", Row(html, Draft), StringComparison.Ordinal);
        Assert.Contains("3 holds", crimp, StringComparison.Ordinal);
        Assert.Contains("by Alice", crimp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShareLink_ShowsNoMarks_AndKeepsItsTokenInEveryLink()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(Anon), $"walls/{Wall}/shared/tok");
        var html = await RenderAsync(view, "tok");

        Assert.DoesNotContain("status-icon", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"walls/{Wall}/boulders/{Crimp}/shared/tok\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("My Walls", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABoulderGoneToDraft_LeavesAnOutsidersList_AtOnce()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(Anon, withDraft: false), $"walls/{Wall}/shared/tok");
        Assert.Contains("Crimp Line", await RenderAsync(view, "tok"), StringComparison.Ordinal);

        view.Store.Apply(Anon, Envelope(FeedKinds.BoulderChanged, Crimp, Crimp, new BoulderPayload(2, true)));
        var html = await view.WaitForAsync(h => !h.Contains("Crimp Line", StringComparison.Ordinal));

        Assert.DoesNotContain("Crimp Line", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWallTheDeviceDoesNotHold_SaysSo()
    {
        var other = Guid.NewGuid();
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), $"walls/{other}");
        var html = await view.RenderAsync<WallViewPage>(new() { ["WallId"] = other });

        Assert.Contains("This wall is not available offline", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"/walls/{other}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWallWithoutPanels_ShowsItsSinglePhoto()
    {
        var source = CachedDevice(UserA);
        var single = WallDoc() with { Panels = [], PhotoSha = PanelSha('9') };
        var holds = HoldsDoc() with { Holds = [HoldsDoc().Holds[0] with { PanelId = null }] };
        Put(source, UserA, new()
        {
            [DataDocumentKinds.Wall] = new(Sha('a'), Json(single)),
            [DataDocumentKinds.Holds] = new(Sha('h'), Json(holds)),
        });
        await using var view = await ViewHarness.StartAsync(source, $"walls/{Wall}");
        var html = await RenderAsync(view);

        Assert.Contains("data-bw-panel=\"photo\"", html, StringComparison.Ordinal);
        Assert.Contains($"/a/{PanelSha('9')}/1920", html, StringComparison.Ordinal);
        Assert.DoesNotContain("panel-arrow", html, StringComparison.Ordinal);
        Assert.Contains("Boulders are not saved on this device.", html, StringComparison.Ordinal);
    }

    // The markup of one boulder's row.
    private static string Row(string html, Guid boulder)
    {
        var start = html.IndexOf($"data-bw-boulder=\"{boulder}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no row for {boulder}");
        return html[start..html.IndexOf("</a>", start, StringComparison.Ordinal)];
    }

    private static Task<string> RenderAsync(ViewHarness view, string? token = null) =>
        view.RenderAsync<WallViewPage>(new() { ["WallId"] = Wall, ["ShareToken"] = token });
}
