// <copyright file="SuggestedColorBadgeTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text.RegularExpressions;
using Blocwerk.Core.Entities;
using Blocwerk.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The suggested-colour "?" marks covered the outlines owners were tracing: fixed-size, they grew
/// with the zoom, and they straddled the hold's edge. They must never take a tap, must shrink with
/// the zoom, and must sit outside the hold.
/// </summary>
public class SuggestedColorBadgeTests
{
    private const double HoldRadius = 1.5;

    [Fact]
    public void NeverTakesAPointerEvent()
    {
        var html = Render(SuggestedHold(), scale: 1.0);

        Assert.Contains("pointer-events=\"none\"", html);
        Assert.Contains(">?</text>", html);
    }

    [Fact]
    public void ShrinksWithTheZoom()
    {
        var atOne = BadgeRadius(Render(SuggestedHold(), scale: 1.0));
        var atEight = BadgeRadius(Render(SuggestedHold(), scale: 1.0 / 8.0));

        Assert.Equal(HoldBadgeGeometry.BaseRadius, atOne, 3);
        Assert.Equal(atOne / 8.0, atEight, 3);
    }

    [Fact]
    public void NeverGrowsWhenZoomedOut()
    {
        Assert.Equal(HoldBadgeGeometry.BaseRadius, HoldBadgeGeometry.Radius(2.0), 6);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.25)]
    [InlineData(1.0 / 12.0)]
    public void SitsOutsideTheHold(double scale)
    {
        var hold = SuggestedHold();
        var html = Render(hold, scale);
        var cx = Attr(html, "cx");
        var cy = Attr(html, "cy");
        var distance = Math.Sqrt(Math.Pow(cx - (hold.X * 100), 2) + Math.Pow(cy - (hold.Y * 100), 2));

        Assert.True(distance >= HoldRadius + BadgeRadius(html) - 1e-3, $"badge centre {distance} inside the hold");
    }

    [Fact]
    public void ConfirmedColourHasNoMark()
    {
        var hold = SuggestedHold();
        hold.ProposedColor = null;

        Assert.DoesNotContain("<text", Render(hold, scale: 1.0));
    }

    private static Hold SuggestedHold() => new() { X = 0.4, Y = 0.6, Radius = 0.015, Color = "red", ProposedColor = "red" };

    private static double BadgeRadius(string html) => Attr(html, "r");

    private static double Attr(string html, string name)
    {
        var match = Regex.Match(html, $"<circle[^>]*\\s{name}=\"([0-9.\\-]+)\"");
        Assert.True(match.Success, $"no {name} in {html}");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static string Render(Hold hold, double scale)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        var parameters = new Dictionary<string, object?>
        {
            [nameof(SuggestedColorBadge.Hold)] = hold,
            [nameof(SuggestedColorBadge.R)] = HoldRadius,
            [nameof(SuggestedColorBadge.Scale)] = scale,
        };
        return renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<SuggestedColorBadge>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        }).GetAwaiter().GetResult();
    }
}
