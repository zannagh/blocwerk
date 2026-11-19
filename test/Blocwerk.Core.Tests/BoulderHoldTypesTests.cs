// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Tests;

/// <summary>A boulder's dominant hold type: set on it, or inferred from its gripped holds; and counting families per type.</summary>
public sealed class BoulderHoldTypesTests
{
    private const HoldHandType Jug = HoldHandType.Jug, Crimp = HoldHandType.Crimp, Sloper = HoldHandType.Sloper;

    [Fact]
    public void TheCardsAreTheFiveTypes_InOrder() =>
        Assert.Equal(["Jug", "Crimp", "Pinch", "Sloper", "Pocket"], BoulderHoldTypes.Definitions.Select(d => d.Label));

    [Fact]
    public void TheMostCommonTypeWins_WhenAtLeastHalfTheHoldsHaveOne() =>
        Assert.Equal(Crimp, BoulderHoldTypes.Infer([Crimp, Crimp, Jug, null, null]));

    [Fact]
    public void ExactlyHalfTyped_IsEnough() => Assert.Equal(Crimp, BoulderHoldTypes.Infer([Crimp, null]));

    [Fact]
    public void FewerThanHalfTyped_TellsNothing() => Assert.Null(BoulderHoldTypes.Infer([Crimp, Crimp, null, null, null]));

    [Fact]
    public void ATie_TellsNothing() => Assert.Null(BoulderHoldTypes.Infer([Crimp, Jug, Crimp, Jug]));

    [Fact]
    public void NoHolds_TellNothing() => Assert.Null(BoulderHoldTypes.Infer([]));

    [Fact]
    public void ATypeSetOnTheBoulder_WinsOverItsHolds() =>
        Assert.Equal(new EffectiveHoldType(Sloper, false), BoulderHoldTypes.Effective(Sloper, [Crimp, Crimp]));

    [Fact]
    public void Cleared_TheHoldsDecideAgain()
    {
        Assert.Equal(new EffectiveHoldType(Crimp, true), BoulderHoldTypes.Effective(null, [Crimp, Crimp]));
        Assert.Null(BoulderHoldTypes.Effective(null, [Crimp, null, null]));
    }

    [Theory]
    [InlineData(HoldCategory.Hand, HoldUsage.HandAndFoot, true)]
    [InlineData(HoldCategory.Hand, HoldUsage.HandOnly, true)]
    [InlineData(HoldCategory.Hand, HoldUsage.FootOnly, false)]
    [InlineData(HoldCategory.Foot, HoldUsage.HandAndFoot, false)]
    public void OnlyGrippedHoldsCount(HoldCategory category, HoldUsage usage, bool expected) =>
        Assert.Equal(expected, BoulderHoldTypes.IsGripped(category, usage));

    [Theory]
    [InlineData("crimp", HoldHandType.Crimp)]
    [InlineData(" Pocket ", HoldHandType.Pocket)]
    [InlineData("nonsense", null)]
    [InlineData(null, null)]
    public void ASlugNamesItsType(string? slug, HoldHandType? expected) => Assert.Equal(expected, BoulderHoldTypes.FromSlug(slug));

    [Fact]
    public void AFamilyCountsOnceUnderEachOfItsTypes()
    {
        var counts = BoulderHoldTypes.CountBy([[Crimp, Jug], [Crimp, Crimp], [], null]);

        Assert.Equal(2, counts[Crimp]);
        Assert.Equal(1, counts[Jug]);
        Assert.Equal(0, counts[Sloper]);
    }

    [Fact]
    public void APublishedVariantsType_FilesTheFamily_ButADraftOrHistoricOneDoesNot()
    {
        var parent = Guid.NewGuid();
        var rows = BoulderFamilies.Rows(
        [
            new(parent, null, "Parent", "6A", false, false, HoldType: Jug),
            new(Guid.NewGuid(), parent, "Variant", "6B", false, false, HoldType: Crimp),
            new(Guid.NewGuid(), parent, "Draft", "6C", true, false, HoldType: Sloper),
            new(Guid.NewGuid(), parent, "Old", "6C", false, false, IsHistoric: true, HoldType: HoldHandType.Pocket),
        ]);

        Assert.Equal([Jug, Crimp], rows[parent].HoldTypes);
    }
}
