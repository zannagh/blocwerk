// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.RegularExpressions;
using Blocwerk.Client.Pages;
using Blocwerk.Client.Views;
using Microsoft.AspNetCore.Components;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientViews.ViewWorld;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>
/// Offline is read-only (plan §2): no view of the client renders a control that edits, for a member's device (drafts,
/// marks), a share link or a kiosk. The only controls are the panel chevrons, "Show all holds" and the list's filters; every link stays
/// in the client or opens a Server read page; nothing carries the Server's offline write actions.
/// </summary>
public sealed partial class NoEditControlTests
{
    private static readonly string[] AllowedButtons = ["panel-arrow", "holds-all-tool", "settings-card-summary", "scale-btn"];
    private static readonly string[] AllowedSelects = ["filter-select", "grade-select"];

    public static TheoryData<string> Partitions => new() { UserA, Anon, Kiosk };

    [Theory]
    [MemberData(nameof(Partitions))]
    public async Task NoViewRendersAnEditControl(string partition)
    {
        var token = partition == Anon ? "tok" : null;
        foreach (var html in await RenderAllAsync(partition, token))
        {
            AssertReadOnly(html);
        }
    }

    [Fact]
    public async Task TheOpenFilterCard_OnlyFilters()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), $"walls/{Wall}");
        var html = await view.RenderAsync<BoulderFilters>(new() { ["Filter"] = new BoulderFilter(), ["ShowMine"] = true, ["StartOpen"] = true });

        Assert.Contains("Done by me", html, StringComparison.Ordinal);
        AssertReadOnly(html);
    }

    [Fact]
    public void TheCheck_CatchesTheServersEditControls()
    {
        Assert.ThrowsAny<Exception>(() => AssertReadOnly("<button type=\"button\" class=\"attempt-btn send\" data-bw-action=\"attempt\">Send</button>"));
        Assert.ThrowsAny<Exception>(() => AssertReadOnly("<a href=\"/walls/x/boulders/y/revise\" class=\"btn-revise\">Revise</a>"));
        Assert.ThrowsAny<Exception>(() => AssertReadOnly("<textarea class=\"comment-input\"></textarea>"));
        Assert.ThrowsAny<Exception>(() => AssertReadOnly("<input class=\"boulder-name-input\" />"));
    }

    private static void AssertReadOnly(string html)
    {
        Assert.DoesNotMatch("<form|<textarea|contenteditable|data-bw-action|data-bw-keep|data-bw-on", html);
        foreach (Match button in Tag("button").Matches(html))
        {
            Assert.True(AllowedButtons.Any(c => ClassOf(button.Value).Contains(c, StringComparison.Ordinal)), $"edit control? {button.Value}");
        }

        foreach (Match input in Tag("input").Matches(html))
        {
            var ok = ClassOf(input.Value).Contains("filter-input", StringComparison.Ordinal) || input.Value.Contains("type=\"checkbox\"", StringComparison.Ordinal);
            Assert.True(ok, $"edit control? {input.Value}");
        }

        foreach (Match select in Tag("select").Matches(html))
        {
            Assert.True(AllowedSelects.Any(c => ClassOf(select.Value).Contains(c, StringComparison.Ordinal)), $"edit control? {select.Value}");
        }

        foreach (Match link in Href().Matches(html))
        {
            var href = link.Groups[1].Value;
            Assert.DoesNotMatch("(revise|create|update|edit|settings|/api/|kiosk|account)", href);
        }
    }

    private static async Task<List<string>> RenderAllAsync(string partition, string? token)
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(partition), $"walls/{Wall}");
        var pages = new List<string>
        {
            await view.RenderAsync<WallListPage>([]),
            await view.RenderAsync<WallViewPage>(new() { ["WallId"] = Wall, ["ShareToken"] = token }),
        };
        foreach (var boulder in new[] { Crimp, Slab, Draft, Guid.NewGuid() })
        {
            pages.Add(await view.RenderAsync<BoulderPage>(new() { ["WallId"] = Wall, ["BoulderId"] = boulder, ["ShareToken"] = token }));
        }

        Assert.Contains(pages, p => p.Contains("Lovely crimps", StringComparison.Ordinal));
        return pages;
    }

    private static string ClassOf(string tag) => ClassAttribute().Match(tag) is { Success: true } m ? m.Groups[1].Value : string.Empty;

    private static Regex Tag(string name) => new($"<{name}\\b[^>]*>", RegexOptions.IgnoreCase);

    [GeneratedRegex("class=\"([^\"]*)\"")]
    private static partial Regex ClassAttribute();

    [GeneratedRegex("href=\"([^\"]*)\"")]
    private static partial Regex Href();
}
