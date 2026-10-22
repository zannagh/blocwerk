// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>The 3D view's mobile-data notice: the size it names, and its two answers.</summary>
public class Wall3DDataNoticeTests
{
    [Theory]
    [InlineData(450_000, "1 MB")]
    [InlineData(7_100_000, "7.1 MB")]
    [InlineData(48_600_000, "49 MB")]
    [InlineData(1_250_000_000, "1.3 GB")]
    public void TheSize_IsRoundedAsPhonesShowDataUse(long bytes, string expected)
    {
        Assert.Equal(expected, Wall3DDataNotice.SizeText(bytes));
    }

    [Fact]
    public async Task TheNotice_NamesTheDownload_AndOffersContinueCancelAndDontAskAgain()
    {
        var sized = await RenderAsync(7_100_000);
        var unknown = await RenderAsync(null);

        Assert.Contains("3D can use a lot of mobile data", sized, StringComparison.Ordinal);
        Assert.Contains("about 7.1 MB", sized, StringComparison.Ordinal);
        Assert.Contains("Continue", sized, StringComparison.Ordinal);
        Assert.Contains("Cancel", sized, StringComparison.Ordinal);
        Assert.Contains("ask again", sized, StringComparison.Ordinal);
        Assert.DoesNotContain("about", unknown, StringComparison.Ordinal);
        Assert.Contains("3D model and photos", unknown, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync(long? bytes)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { ["Bytes"] = bytes });
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<Wall3DDataNotice>(parameters)).ToHtmlString());
    }
}
