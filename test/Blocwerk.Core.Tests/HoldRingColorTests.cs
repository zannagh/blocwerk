// <copyright file="HoldRingColorTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Holds;
using Blocwerk.Web.Components.Shared;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="HoldRingColor"/>: a hold's free-text colour lands in an inline <c>style</c>, so only a
/// palette key or a strict hex colour may ever come out.
/// </summary>
public class HoldRingColorTests
{
    [Theory]
    [InlineData("#fff")]
    [InlineData("#A0703C")]
    [InlineData("#a0703c80")]
    public void Style_StrictHex_IsEmitted(string color)
    {
        Assert.Equal($"--ring:{color}", HoldRingColor.Style(color));
    }

    [Fact]
    public void Style_PaletteKey_MapsToItsHex()
    {
        Assert.Equal($"--ring:{HoldPalette.Hex("wood-medium")}", HoldRingColor.Style("wood-medium"));
        Assert.Equal($"--ring:{HoldPalette.Hex("red")}", HoldRingColor.Style("red"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#12")]
    [InlineData("#123456789")]
    [InlineData("#fff;background:url(x)")]
    [InlineData("#fff\";onload")]
    [InlineData("red;position:fixed")]
    [InlineData("cyan")]
    public void Style_AnythingElse_FallsBackToTheDefaultRing(string? color)
    {
        Assert.Equal(string.Empty, HoldRingColor.Style(color));
    }
}
