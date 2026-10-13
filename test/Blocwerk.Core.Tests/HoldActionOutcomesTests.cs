// <copyright file="HoldActionOutcomesTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared;

namespace Blocwerk.Core.Tests;

/// <summary>
/// What the tools that change the wall model say before they act: which hold stays, how many boulders
/// are affected (named up to five), and whether it can be undone.
/// </summary>
public class HoldActionOutcomesTests
{
    [Fact]
    public void Boulders_AreNamedUpToFive_AndOnlyCountedBeyond()
    {
        var five = HoldActionOutcomes.MarkChanged(Hold("A1"), Usage(5));
        var six = HoldActionOutcomes.MarkChanged(Hold("A1"), Usage(6));

        Assert.Contains("5 boulders (B1, B2, B3, B4, B5) are flagged for review.", five.Boulders);
        Assert.Equal("6 boulders are flagged for review.", six.Boulders);
    }

    [Fact]
    public void ABoulderUsingTheHoldTwice_IsCountedOnce()
    {
        var boulder = Guid.NewGuid();
        var usage = new List<HoldUsageRef> { Ref("Slab", id: boulder), Ref("Slab", id: boulder) };

        Assert.StartsWith("1 boulder (Slab) is flagged", HoldActionOutcomes.MarkChanged(Hold("A1"), usage).Boulders);
    }

    [Fact]
    public void UnknownUsage_IsSaidPlainly_NotReportedAsNone()
    {
        var outcome = HoldActionOutcomes.MakeActualAsIs(Hold(null), null);

        Assert.Equal("Which boulders use it is not known here.", outcome.Boulders);
        Assert.StartsWith("The virtual hold becomes a normal hold", outcome.Holds);
    }

    [Fact]
    public void MergeVirtual_SaysTheVirtualHoldStays_AndTheDetectedOneGoes()
    {
        var outcome = HoldActionOutcomes.MergeVirtual(Hold("V1"), Hold("D7"), Usage(2), Usage(1));

        Assert.StartsWith("“V1” stays and becomes a normal hold.", outcome.Holds);
        Assert.Contains("“D7”, which disappears", outcome.Holds);
        Assert.Contains("2 boulders (B1, B2) keep the hold that stays.", outcome.Boulders);
        Assert.Contains("1 boulder (B1) of the detected hold moves over to it.", outcome.Boulders);
        Assert.True(outcome.Danger);
        Assert.Equal("This cannot be undone.", outcome.Undo);
    }

    [Fact]
    public void JoinVirtual_MovesTheDuplicatesBoulders()
    {
        var outcome = HoldActionOutcomes.JoinVirtual(Hold("Keep"), Hold("Drop"), Usage(3));

        Assert.Equal("“Keep” stays exactly as it is and remains virtual. “Drop” disappears.", outcome.Holds);
        Assert.StartsWith("3 boulders (B1, B2, B3) move over", outcome.Boulders);
    }

    [Fact]
    public void Delete_CountsOnlyBouldersThatAreNotHistoricAlready()
    {
        var usage = new List<HoldUsageRef> { Ref("Current"), Ref("Old", historic: true) };

        var outcome = HoldActionOutcomes.Delete(Hold("A1"), usage);

        Assert.Equal("1 boulder (Current) becomes historic when you save.", outcome.Boulders);
        Assert.Contains("Discard takes it back until you save.", outcome.Undo);
    }

    [Fact]
    public void MarkUnchanged_CountsOnlyHistoricBoulders()
    {
        var usage = new List<HoldUsageRef> { Ref("Current"), Ref("Old", historic: true) };

        Assert.StartsWith("1 boulder (Old) becomes current again", HoldActionOutcomes.MarkUnchanged(Hold("A1"), usage).Boulders);
        Assert.StartsWith("No historic boulder uses it", HoldActionOutcomes.MarkUnchanged(Hold("A1"), [Ref("Current")]).Boulders);
    }

    [Fact]
    public void MarkChangedOnSave_IsADraft_SoItIsNoDanger()
    {
        var drafted = HoldActionOutcomes.MarkChanged(Hold("A1"), Usage(1), onSave: true);

        Assert.False(drafted.Danger);
        Assert.Equal("Discard takes it back until you save.", drafted.Undo);
        Assert.True(HoldActionOutcomes.MarkChanged(Hold("A1"), Usage(1)).Danger);
    }

    [Theory]
    [InlineData(HoldMergeMode.KeepLeft, "The left hold “Hand” stays exactly as it is. The right hold disappears.")]
    [InlineData(HoldMergeMode.KeepRight, "The right hold stays exactly as it is. The left hold “Hand” disappears.")]
    [InlineData(HoldMergeMode.KeepHandUseDetectedShape, "The left hold “Hand” keeps its name and colour and takes the position, outline and size of the detected one. The right hold disappears.")]
    public void DuplicateMerge_NamesTheSideThatStays_AndIsUndoable(HoldMergeMode mode, string expected)
    {
        var item = new HoldDuplicateItem(
            HoldDuplicateKind.InsideHandPlaced,
            0.9,
            new HoldDuplicateHoldInfo(Guid.NewGuid(), "Hand", null, IsHandMade: true, IsVirtual: false, 2, ["One", "Two"]),
            new HoldDuplicateHoldInfo(Guid.NewGuid(), null, null, IsHandMade: false, IsVirtual: false, 1, ["Three"]),
            HoldMergeMode.KeepHandUseDetectedShape,
            3);

        var outcome = HoldActionOutcomes.DuplicateMerge(item, mode);

        Assert.Equal(expected, outcome.Holds);
        Assert.False(outcome.Danger);
        Assert.StartsWith("You can undo it", outcome.Undo);
        Assert.StartsWith(
            mode == HoldMergeMode.KeepRight ? "2 boulders (One, Two) move over" : "1 boulder (Three) moves over",
            outcome.Boulders);
    }

    private static Hold Hold(string? name) => new() { Id = Guid.NewGuid(), Name = name };

    private static List<HoldUsageRef> Usage(int count) =>
        [.. Enumerable.Range(1, count).Select(i => Ref($"B{i}"))];

    private static HoldUsageRef Ref(string name, bool historic = false, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), name, null, HoldType.Normal, HoldUsage.HandAndFoot, IsDraft: false, IsHistoric: historic);
}
