// <copyright file="HoldColorPaletteTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Holds.Colors;
using SkiaSharp;

namespace Blocwerk.Core.Tests.HoldColors;

/// <summary>
/// A warm hold between red and orange is compared with the holds of known colour on its photo: it gets their colour
/// when nearly all of its nearest ones agree, and stays unset otherwise.
/// </summary>
public class HoldColorPaletteTests
{
    // Hue about 35: between red and orange, so the estimate alone leaves it unset.
    private static readonly SKColor Rust = new(210, 90, 60);

    private static readonly (double X, double Y)[] Spots =
        [(0.1, 0.25), (0.3, 0.25), (0.5, 0.25), (0.7, 0.25), (0.9, 0.25), (0.1, 0.7), (0.3, 0.7), (0.5, 0.7), (0.7, 0.7), (0.9, 0.7)];

    [Fact]
    public async Task AHoldBetweenRedAndOrange_TakesTheColourOfTheWallsOwnHoldsLikeIt()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0, photo: RustPhoto());
        foreach (var spot in Spots[..9])
        {
            await s.AddHoldAsync(panel, spot, configure: h => (h.Radius, h.Color) = (0.045, "orange"));
        }

        var open = await s.AddHoldAsync(panel, Spots[9], configure: h => h.Radius = 0.045);

        await s.Service().SuggestMissingAsync(s.WallId);

        var hold = await s.GetAsync(open);
        Assert.Equal(("orange", "orange"), (hold.Color, hold.ProposedColor));
    }

    [Fact]
    public async Task WithoutEnoughHoldsOfKnownColour_TheHoldStaysUnset()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0, photo: RustPhoto());
        foreach (var spot in Spots[..5])
        {
            await s.AddHoldAsync(panel, spot, configure: h => (h.Radius, h.Color) = (0.045, "orange"));
        }

        var open = await s.AddHoldAsync(panel, Spots[9], configure: h => h.Radius = 0.045);

        await s.Service().SuggestMissingAsync(s.WallId);

        Assert.Null((await s.GetAsync(open)).Color);
    }

    [Fact]
    public void NeighboursThatDisagree_APaleHold_OrACoolHue_LeaveTheEstimateAlone()
    {
        var between = HoldColorEstimate.Unset(HoldColorVerdict.BetweenColors);
        var warm = new HoldColorSignature(6, 4, -10, 35, 9);
        var mixed = new HoldColorPalette([.. Many(warm, "orange", 5), .. Many(warm, "red", 4)]);
        var agreeing = new HoldColorPalette([.. Many(warm, "orange", 8), .. Many(warm, "red", 1)]);

        Assert.Null(mixed.Refine(between, warm).Color);
        Assert.Equal("orange", agreeing.Refine(between, warm).Color);
        Assert.Null(agreeing.Refine(between, warm with { Chroma = 5 }).Color);
        Assert.Null(agreeing.Refine(between, warm with { Hue = 120 }).Color);
        Assert.Null(agreeing.Refine(HoldColorEstimate.Unset(HoldColorVerdict.MixedHues), warm).Color);
        Assert.Equal("yellow", agreeing.Refine(new HoldColorEstimate("yellow", HoldColorVerdict.Proposed), warm).Color);
    }

    private static IEnumerable<(HoldColorSignature, string)> Many(HoldColorSignature signature, string color, int count) =>
        Enumerable.Repeat((signature, color), count);

    private static byte[] RustPhoto() => HoldColorTestPhoto.Build([.. Spots.Select(p => (p.X, p.Y, 0.04, Rust))]);
}
