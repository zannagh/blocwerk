// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Pages;
using Blocwerk.Contracts.Documents;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientViews.ViewWorld;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>
/// A wall's kinds of data are cached separately, so a device can hold some and not others. A kind the device does not
/// hold reads "not saved on this device" with the way online, never as a fact about the wall: no "No boulders yet", no
/// "No comments yet", no "N of N holds are not on the wall".
/// </summary>
public sealed class MissingDocumentTests
{
    [Fact]
    public async Task NoBouldersDocument_ReadsNotSaved_NeverNoBouldersYet()
    {
        await using var view = await ViewHarness.StartAsync(PartialDevice(UserA, DataDocumentKinds.Wall, DataDocumentKinds.Holds), $"walls/{Wall}");
        var html = await view.RenderAsync<WallViewPage>(new() { ["WallId"] = Wall });

        Assert.Contains("Boulders are not saved on this device.", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"/walls/{Wall}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No boulders yet", html, StringComparison.Ordinal);
        Assert.Contains($"<span class=\"stat-value\">{System.Text.Encodings.Web.HtmlEncoder.Default.Encode("–")}</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommentsDocument_ReadsNotSaved_NeverNoCommentsYet()
    {
        var path = $"walls/{Wall}/boulders/{Crimp}";
        await using var view = await ViewHarness.StartAsync(PartialDevice(UserA, DataDocumentKinds.Wall, DataDocumentKinds.Holds, DataDocumentKinds.Boulders), path);
        var html = await RenderBoulderAsync(view);

        Assert.Contains("Comments are not saved on this device.", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"/{path}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No comments yet", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoHoldsDocument_ReadsNotSaved_NeverHoldsMissingFromTheWall()
    {
        await using var view = await ViewHarness.StartAsync(PartialDevice(UserA, DataDocumentKinds.Wall, DataDocumentKinds.Boulders), $"walls/{Wall}/boulders/{Crimp}");
        var html = await RenderBoulderAsync(view);

        Assert.Contains("The holds of this boulder are not saved on this device.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("not on the wall", html, StringComparison.Ordinal);
        Assert.DoesNotContain("no such holds on this wall", html, StringComparison.Ordinal);
        Assert.Contains("All red holds may be used as footholds.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyCommentsDocument_StillSaysNoCommentsYet()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA, comments: []), $"walls/{Wall}/boulders/{Crimp}");

        Assert.Contains("No comments yet.", await RenderBoulderAsync(view), StringComparison.Ordinal);
    }

    private static Task<string> RenderBoulderAsync(ViewHarness view) =>
        view.RenderAsync<BoulderPage>(new() { ["WallId"] = Wall, ["BoulderId"] = Crimp });
}
