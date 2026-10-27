// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Pages;
using Blocwerk.Core.Tests.OfflineClientStore;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientViews.ViewWorld;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>The offline wall list (PR 3.11): every wall the device holds, with its cached card photo, and nothing to create or join.</summary>
public sealed class WallListPageTests
{
    [Fact]
    public async Task ListsTheDevicesWalls_WithTheirThumbnail_AndLinksIntoTheClient()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), "walls");
        var html = await view.RenderAsync<WallListPage>([]);

        Assert.Contains("<h1>My Walls</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<h3>The Attic</h3>", html, StringComparison.Ordinal);
        Assert.Contains("2 boulders", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"walls/{Wall}\"", html, StringComparison.Ordinal);
        Assert.Contains($"src=\"/api/walls/{Wall}/a/{PanelSha('1')}/640\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("New Wall", html, StringComparison.Ordinal);
        Assert.DoesNotContain("join", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADeviceWithoutWalls_SaysHowToSaveOne()
    {
        await using var view = await ViewHarness.StartAsync(new FakeStoreSource(), string.Empty);
        var html = await view.RenderAsync<WallListPage>([]);

        Assert.Contains("No walls saved on this device yet.", html, StringComparison.Ordinal);
    }
}
