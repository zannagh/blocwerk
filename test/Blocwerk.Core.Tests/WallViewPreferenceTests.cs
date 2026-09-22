// <copyright file="WallViewPreferenceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Web.Components.Shared;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The wall-editor view cookie is a hand-rolled format shared between JS (which stores it) and the
/// server (which reads it off the prerender request), so its edges are worth pinning down: an old,
/// truncated or tampered-with cookie must degrade to the default rather than throw or, worse, come
/// back as a destructive tool.
/// </summary>
public class WallViewPreferenceTests
{
    [Fact]
    public void RoundTripsEveryField()
    {
        var original = new WallViewPreference(
            ShowNames: true,
            ShowBorder: false,
            ShowChanges: true,
            HoldCompletenessCriteria.Color | HoldCompletenessCriteria.HandType,
            HoldTouchupTool.Move);

        Assert.Equal(original, WallViewPreference.Parse(original.ToCookieValue()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("n.b.c.i")]
    [InlineData("....")]
    [InlineData("nx.bz.i!.tNotATool")]
    public void FallsBackToDefaultOnJunk(string? stored)
    {
        Assert.Equal(WallViewPreference.Default, WallViewPreference.Parse(stored));
    }

    [Fact]
    public void KeepsDefaultsForKeysTheCookieOmits()
    {
        // A cookie written before the tool key existed must still read cleanly.
        var parsed = WallViewPreference.Parse("n1.b0.c0.i1");

        Assert.True(parsed.ShowNames);
        Assert.False(parsed.ShowBorder);
        Assert.False(parsed.ShowChanges);
        Assert.Equal(HoldCompletenessCriteria.Color, parsed.IncompleteCriteria);
        Assert.Equal(WallViewPreference.Default.Tool, parsed.Tool);
    }

    [Theory]
    [InlineData(HoldTouchupTool.Delete)]
    [InlineData(HoldTouchupTool.MarkChanged)]
    [InlineData(HoldTouchupTool.MarkUnchanged)]
    [InlineData(HoldTouchupTool.Merge)]
    [InlineData(HoldTouchupTool.MakeActual)]
    [InlineData(HoldTouchupTool.JoinVirtual)]
    public void NeverRestoresADestructiveOrStagedOnlyTool(HoldTouchupTool tool)
    {
        Assert.False(WallViewPreference.IsRestorable(tool));

        // Even if the cookie names one outright — an older build, or a hand-edited cookie.
        Assert.Equal(WallViewPreference.Default.Tool, WallViewPreference.Parse($"t{tool}").Tool);

        // And storing one never writes it back out.
        var stored = WallViewPreference.Default with { Tool = tool };
        Assert.Equal(WallViewPreference.Default.Tool, WallViewPreference.Parse(stored.ToCookieValue()).Tool);
    }

    [Theory]
    [InlineData(HoldTouchupTool.Move)]
    [InlineData(HoldTouchupTool.Add)]
    [InlineData(HoldTouchupTool.Paint)]
    [InlineData(HoldTouchupTool.ShapeTilt)]
    public void RestoresAnEverydayTool(HoldTouchupTool tool)
    {
        Assert.True(WallViewPreference.IsRestorable(tool));
        Assert.Equal(tool, WallViewPreference.Parse($"t{tool}").Tool);
    }

    [Fact]
    public void MasksCriteriaBitsThisBuildDoesNotDefine()
    {
        Assert.Equal(
            HoldCompletenessCriteria.Color | HoldCompletenessCriteria.HandType,
            WallViewPreference.Parse("i255").IncompleteCriteria);
    }

    [Fact]
    public void DefaultMatchesAnUnsetCookie()
    {
        // An unset cookie must not change what the editor looks like.
        Assert.Equal(WallViewPreference.Default, WallViewPreference.Parse(string.Empty));
    }
}
