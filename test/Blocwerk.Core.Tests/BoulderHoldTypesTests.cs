// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Tests;

/// <summary>A boulder's hold types: set on it (any combination), or one grip inferred from its gripped holds; and counting families per type.</summary>
public sealed class BoulderHoldTypesTests
{
    private const HoldHandType Jug = HoldHandType.Jug, Crimp = HoldHandType.Crimp;
    private const BoulderHoldType BJug = BoulderHoldType.Jug, BCrimp = BoulderHoldType.Crimp, BSloper = BoulderHoldType.Sloper;
    private const BoulderHoldType Heel = BoulderHoldType.HeelHook, Toe = BoulderHoldType.ToeHook;

    [Fact]
    public void TheCardsAreTheFiveGripsAndTheTwoHooks_InOrder() =>
        Assert.Equal(["Jug", "Crimp", "Pinch", "Sloper", "Pocket", "Heel hook", "Toe hook"], BoulderHoldTypes.Definitions.Select(d => d.Label));

    [Fact]
    public void EveryType_HasALabelAnIconAndADescription_AndItsOwnBit()
    {
        Assert.All(BoulderHoldTypes.Definitions, d => Assert.False(string.IsNullOrEmpty(d.Icon) || string.IsNullOrEmpty(d.Description)));
        Assert.Equal(127, (int)BoulderHoldTypes.Mask);
        Assert.Equal(31, (int)BoulderHoldTypes.Grips);
    }

    [Fact]
    public void TheHooksSlugsAreHyphenated() =>
        Assert.Equal(["heel-hook", "toe-hook"], new[] { Heel, Toe }.Select(BoulderHoldTypes.Slug));

    [Fact]
    public void TheMostCommonTypeWins_WhenAtLeastHalfTheHoldsHaveOne() =>
        Assert.Equal(BCrimp, BoulderHoldTypes.Infer([Crimp, Crimp, Jug, null, null]));

    [Fact]
    public void ExactlyHalfTyped_IsEnough() => Assert.Equal(BCrimp, BoulderHoldTypes.Infer([Crimp, null]));

    [Fact]
    public void FewerThanHalfTyped_TellsNothing() => Assert.Null(BoulderHoldTypes.Infer([Crimp, Crimp, null, null, null]));

    [Fact]
    public void ATie_TellsNothing() => Assert.Null(BoulderHoldTypes.Infer([Crimp, Jug, Crimp, Jug]));

    [Fact]
    public void NoHolds_TellNothing() => Assert.Null(BoulderHoldTypes.Infer([]));

    [Fact]
    public void EveryHandTypeMapsToItsGrip() =>
        Assert.All(Enum.GetValues<HoldHandType>(), h => Assert.Equal(h.ToString(), BoulderHoldTypes.FromHand(h).ToString()));

    [Fact]
    public void TypesSetOnTheBoulder_WinOverItsHolds() =>
        Assert.Equal(new EffectiveHoldType(BSloper | Heel, false), BoulderHoldTypes.Effective(BSloper | Heel, [Crimp, Crimp]));

    [Fact]
    public void OnlyAHookSet_StillWinsOverTheHolds() =>
        Assert.Equal(new EffectiveHoldType(Toe, false), BoulderHoldTypes.Effective(Toe, [Crimp, Crimp]));

    [Fact]
    public void NoneSet_TheHoldsDecide()
    {
        Assert.Equal(new EffectiveHoldType(BCrimp, true), BoulderHoldTypes.Effective(BoulderHoldType.None, [Crimp, Crimp]));
        Assert.Null(BoulderHoldTypes.Effective(BoulderHoldType.None, [Crimp, null, null]));
    }

    [Theory]
    [InlineData(HoldCategory.Hand, HoldUsage.HandAndFoot, true)]
    [InlineData(HoldCategory.Hand, HoldUsage.HandOnly, true)]
    [InlineData(HoldCategory.Hand, HoldUsage.FootOnly, false)]
    [InlineData(HoldCategory.Foot, HoldUsage.HandAndFoot, false)]
    public void OnlyGrippedHoldsCount(HoldCategory category, HoldUsage usage, bool expected) =>
        Assert.Equal(expected, BoulderHoldTypes.IsGripped(category, usage));

    [Theory]
    [InlineData("crimp", BoulderHoldType.Crimp)]
    [InlineData(" Pocket ", BoulderHoldType.Pocket)]
    [InlineData("heel-hook", BoulderHoldType.HeelHook)]
    [InlineData("Heel hook", BoulderHoldType.HeelHook)]
    [InlineData("ToeHook", BoulderHoldType.ToeHook)]
    [InlineData("nonsense", null)]
    [InlineData("none", null)]
    [InlineData(null, null)]
    public void ASlugNamesItsType(string? slug, BoulderHoldType? expected) => Assert.Equal(expected, BoulderHoldTypes.FromSlug(slug));

    [Fact]
    public void NamesRoundTrip_AndAnUnknownNameIsIgnored()
    {
        var set = BCrimp | Heel | Toe;
        Assert.Equal(["Crimp", "Heel hook", "Toe hook"], BoulderHoldTypes.ToNames(set));
        Assert.Equal(set, BoulderHoldTypes.FromNames(BoulderHoldTypes.ToNames(set)));
        Assert.Equal(BJug, BoulderHoldTypes.FromNames(["Jug", "Bogus", null]));
    }

    [Fact]
    public void AFamilyCountsOnceUnderEachOfItsTypes()
    {
        var counts = BoulderHoldTypes.CountBy([BCrimp | BJug, BCrimp | Heel, BoulderHoldType.None]);

        Assert.Equal(2, counts[BCrimp]);
        Assert.Equal(1, counts[BJug]);
        Assert.Equal(1, counts[Heel]);
        Assert.Equal(0, counts[BSloper]);
    }

    [Fact]
    public void APublishedVariantsTypes_FileTheFamily_ButADraftOrHistoricOneDoesNot()
    {
        var parent = Guid.NewGuid();
        var rows = BoulderFamilies.Rows(
        [
            new(parent, null, "Parent", "6A", false, false, HoldTypes: BJug),
            new(Guid.NewGuid(), parent, "Variant", "6B", false, false, HoldTypes: BCrimp | Heel),
            new(Guid.NewGuid(), parent, "Draft", "6C", true, false, HoldTypes: BSloper),
            new(Guid.NewGuid(), parent, "Old", "6C", false, false, IsHistoric: true, HoldTypes: Toe),
        ]);

        Assert.Equal(BJug | BCrimp | Heel, rows[parent].HoldTypes);
    }
}
