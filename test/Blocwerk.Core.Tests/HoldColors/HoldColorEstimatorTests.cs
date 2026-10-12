// <copyright file="HoldColorEstimatorTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Holds;
using Blocwerk.Core.Holds.Colors;

namespace Blocwerk.Core.Tests.HoldColors;

/// <summary>
/// The colour estimate on synthetic patches: it names a clear colour through chalk and shadow, and says nothing when
/// the patch is mostly chalk, wooden, grey or between two colours.
/// </summary>
public class HoldColorEstimatorTests
{
    private static readonly (byte R, byte G, byte B) Wood = (222, 196, 160);
    private static readonly (byte R, byte G, byte B) Chalk = (236, 234, 230);

    [Theory]
    [InlineData("yellow", 232, 200, 20)]
    [InlineData("red", 200, 45, 50)]
    [InlineData("pink", 228, 70, 140)]
    [InlineData("orange", 230, 120, 30)]
    [InlineData("blue", 40, 90, 200)]
    [InlineData("green", 50, 160, 70)]
    [InlineData("purple", 130, 60, 180)]
    [InlineData("white", 235, 235, 232)]
    [InlineData("black", 28, 28, 30)]
    public void AClearHoldOnTheWoodenWall_GetsItsColour(string expected, byte r, byte g, byte b)
    {
        var estimate = HoldColorEstimator.Estimate(Patch(400, (r, g, b)), Patch(300, Wood));

        Assert.Equal((expected, HoldColorVerdict.Proposed), (estimate.Color, estimate.Verdict));
    }

    [Fact]
    public void EveryColourItCanPropose_IsAPlasticPaletteColour()
    {
        Assert.All(HoldColorEstimator.Proposable, key => Assert.True(HoldPalette.IsValidFor(key, HoldMaterial.PU), key));
        Assert.All(HoldColorEstimator.Proposable, key => Assert.Contains(HoldPalette.PlasticColors, c => c.Key == key));
    }

    [Fact]
    public void AChalkyHold_KeepsItsColour_UntilChalkIsMostOfIt()
    {
        var chalky = HoldColorEstimator.Estimate([.. Patch(240, (40, 90, 200)), .. Patch(160, Chalk)], Patch(300, Wood));
        var buried = HoldColorEstimator.Estimate([.. Patch(80, (40, 90, 200)), .. Patch(320, Chalk)], Patch(300, Wood));

        Assert.Equal("blue", chalky.Color);
        Assert.Equal((null, HoldColorVerdict.MostlyChalk), (buried.Color, buried.Verdict));
    }

    [Fact]
    public void AShadowedHold_KeepsItsColour()
    {
        // Half of the hold in its own shadow: darker and duller, the same hue.
        var estimate = HoldColorEstimator.Estimate([.. Patch(200, (50, 160, 70)), .. Patch(200, (22, 70, 31))], Patch(300, Wood));

        Assert.Equal("green", estimate.Color);
    }

    [Fact]
    public void AWoodenHoldOnTheWoodenWall_StaysUnset()
    {
        var sameWood = HoldColorEstimator.Estimate(Patch(400, Wood), Patch(300, Wood));
        var darkerWood = HoldColorEstimator.Estimate(Patch(400, (150, 110, 70)), Patch(300, Wood));
        var bleachedWood = HoldColorEstimator.Estimate(Patch(400, (206, 196, 178)), Patch(300, Wood));

        Assert.Equal((null, HoldColorVerdict.LikeSurroundings), (sameWood.Color, sameWood.Verdict));
        Assert.Null(darkerWood.Color);
        Assert.Null(bleachedWood.Color);
    }

    [Fact]
    public void AHueBetweenTwoPaletteColours_StaysUnset()
    {
        // Coral sits between pink and red, amber between orange and yellow, indigo between blue and purple.
        Assert.All(
            new (byte, byte, byte)[] { (225, 75, 95), (235, 165, 20), (75, 70, 210) },
            rgb =>
            {
                var estimate = HoldColorEstimator.Estimate(Patch(400, rgb), Patch(300, Wood));
                Assert.Equal((null, HoldColorVerdict.BetweenColors), (estimate.Color, estimate.Verdict));
            });
    }

    [Fact]
    public void TwoColoursInOneOutline_StayUnset()
    {
        var estimate = HoldColorEstimator.Estimate([.. Patch(200, (200, 45, 50)), .. Patch(200, (40, 90, 200))], Patch(300, Wood));

        Assert.Equal((null, HoldColorVerdict.MixedHues), (estimate.Color, estimate.Verdict));
    }

    [Fact]
    public void GreyAndDarkBlue_AreNotCalledWhiteOrBlack()
    {
        var grey = HoldColorEstimator.Estimate(Patch(400, (128, 128, 130)), Patch(300, Wood));
        var chalkedBlack = HoldColorEstimator.Estimate([.. Patch(220, (28, 28, 30)), .. Patch(180, Chalk)], Patch(300, Wood));
        var navy = HoldColorEstimator.Estimate(Patch(400, (30, 34, 46)), Patch(300, Wood));

        Assert.Equal((null, HoldColorVerdict.UnclearGrey), (grey.Color, grey.Verdict));
        Assert.Equal((null, HoldColorVerdict.UnclearGrey), (chalkedBlack.Color, chalkedBlack.Verdict));
        Assert.NotEqual("black", navy.Color);
    }

    [Fact]
    public void AFadedWarmHold_IsNotCalledOrange_BecauseWoodLooksLikeThat()
    {
        var estimate = HoldColorEstimator.Estimate(Patch(400, (200, 160, 140)), Patch(300, Wood));

        Assert.Equal((null, HoldColorVerdict.Pale), (estimate.Color, estimate.Verdict));
    }

    [Fact]
    public void AHandfulOfPixels_SaysNothing_AndNoSurroundingsStillWorks()
    {
        var tiny = HoldColorEstimator.Estimate(Patch(HoldColorEstimator.MinPixels - 1, (40, 90, 200)), Patch(300, Wood));
        var alone = HoldColorEstimator.Estimate(Patch(400, (40, 90, 200)), []);

        Assert.Equal((null, HoldColorVerdict.TooFewPixels), (tiny.Color, tiny.Verdict));
        Assert.Equal("blue", alone.Color);
    }

    [Fact]
    public void LinkedViews_AgreeOrTheClearlyLargerOneWins()
    {
        var blueBig = new HoldColorEstimate("blue", HoldColorVerdict.Proposed, 900);
        var blueSmall = new HoldColorEstimate("blue", HoldColorVerdict.Proposed, 200);
        var purpleSmall = new HoldColorEstimate("purple", HoldColorVerdict.Proposed, 200);
        var purpleClose = new HoldColorEstimate("purple", HoldColorVerdict.Proposed, 700);
        var unsure = new HoldColorEstimate(null, HoldColorVerdict.MostlyChalk, 2000);

        Assert.Equal("blue", LinkedHoldColorRule.Choose([blueSmall, blueBig]).Color);
        Assert.Equal("blue", LinkedHoldColorRule.Choose([unsure, blueSmall]).Color);
        Assert.Equal("blue", LinkedHoldColorRule.Choose([purpleSmall, blueBig]).Color);
        Assert.Equal((null, HoldColorVerdict.ViewsDisagree), Verdict(LinkedHoldColorRule.Choose([purpleClose, blueBig])));
        Assert.Equal((null, HoldColorVerdict.MostlyChalk), Verdict(LinkedHoldColorRule.Choose([unsure, HoldColorEstimate.Unset(HoldColorVerdict.Pale)])));
    }

    private static (string? Color, HoldColorVerdict Verdict) Verdict(HoldColorEstimate estimate) => (estimate.Color, estimate.Verdict);

    // A patch with a little brightness texture, as a photo has.
    private static List<OklabColor> Patch(int count, (byte R, byte G, byte B) rgb) =>
        Enumerable.Range(0, count)
            .Select(i => (i % 7) - 3)
            .Select(d => OklabColor.FromRgb(Shade(rgb.R, d), Shade(rgb.G, d), Shade(rgb.B, d)))
            .ToList();

    private static byte Shade(byte channel, int delta) => (byte)Math.Clamp(channel + delta, 0, 255);
}
