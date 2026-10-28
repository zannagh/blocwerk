// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Pages;
using static Blocwerk.Core.Tests.OfflineClient3D.View3DWorld;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientViews.ViewWorld;

namespace Blocwerk.Core.Tests.OfflineClient3D;

/// <summary>
/// The 3D page (<c>/v/walls/{w}/3d</c> and its share twin, the Server's routes): the device's 3D of the wall, a boulder
/// highlighted by <c>?boulder=</c>, back to where it came from; a wall the device does not hold is not saved; the 3D
/// opt-out and a model not cached in full say so.
/// </summary>
public sealed class Wall3DPageTests
{
    [Fact]
    public async Task TheWallsModel_Shows_WithTheBouldersNameAndLegend()
    {
        var client = new FakeClient3D { Status = Ready };
        await using var view = await StartAsync(Device3D(UserA), $"walls/{Wall}/3d?boulder={Crimp}&mode=photoreal", client);

        var html = await view.RenderAsync<Wall3DPage>(new() { ["WallId"] = Wall });

        Assert.Contains("data-bw-3d=\"ready\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"wall3d-boulder\">Crimp Line<", html, StringComparison.Ordinal);
        Assert.Contains("wall3d-legend", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"walls/{Wall}/boulders/{Crimp}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWallsView_GoesBackToTheWall_AndAnUnknownBoulderHighlightsNothing()
    {
        await using var view = await StartAsync(Device3D(Anon), $"walls/{Wall}/shared/tok/3d?boulder={Guid.NewGuid()}", new FakeClient3D { Status = Ready });

        var html = await view.RenderAsync<Wall3DPage>(new() { ["WallId"] = Wall, ["ShareToken"] = "tok" });

        Assert.Contains($"href=\"walls/{Wall}/shared/tok\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("wall3d-boulder", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("off", "3D is turned off on this device")]
    [InlineData("missing", "not saved on this device")]
    [InlineData("none", "no 3D model on this device")]
    public async Task WhatTheDeviceCannotShow_SaysWhy(string state, string text)
    {
        var client = new FakeClient3D { Status = new(state, null, false) };
        await using var view = await StartAsync(Device3D(UserA), $"walls/{Wall}/3d", client);

        var html = await view.RenderAsync<Wall3DPage>(new() { ["WallId"] = Wall });

        Assert.Contains(text, html, StringComparison.Ordinal);
        Assert.DoesNotContain("wall3d-stage-el", html, StringComparison.Ordinal);
        Assert.Empty(client.Mounts);
    }

    [Fact]
    public async Task AWallTheDeviceDoesNotHold_IsNotSaved()
    {
        var client = new FakeClient3D { Status = Ready };
        await using var view = await StartAsync(Device3D(UserA), $"walls/{Guid.Empty}/3d", client);

        var html = await view.RenderAsync<Wall3DPage>(new() { ["WallId"] = Guid.NewGuid() });

        Assert.Contains("data-bw-not-saved", html, StringComparison.Ordinal);
        Assert.Empty(client.StatusReads);
    }
}
