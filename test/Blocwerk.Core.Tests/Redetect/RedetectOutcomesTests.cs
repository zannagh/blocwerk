// <copyright file="RedetectOutcomesTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Holds.Colors;
using Blocwerk.Core.Redetect;
using Blocwerk.Web.Components.Shared;

namespace Blocwerk.Core.Tests.Redetect;

/// <summary>
/// What the re-detect confirm says before anything changes, and the colour rule behind "the same hold": a missing
/// colour and two colours that are easily mixed up say nothing, two clearly different ones say "different".
/// </summary>
public class RedetectOutcomesTests
{
    [Fact]
    public void TheConfirm_SaysWhatHappens_NamesTheMerges_AndIsRedOnlyWhenHoldsAreRemoved()
    {
        var merges = Enumerable.Range(0, 7)
            .Select(i => new RedetectMergeInfo(Guid.NewGuid(), Guid.NewGuid(), i == 1 ? "Jug" : null, i == 2 ? null : "red", 0))
            .ToList();

        var outcome = RedetectOutcomes.Confirm(new PanelRedetectSummary(40, 12, 1, 3, merges, 2, 1, 4, 0));
        var harmless = RedetectOutcomes.Confirm(new PanelRedetectSummary(40, 1, 0, 0, [], 0, 0, 0, 0));

        Assert.Contains("12 holds get the newly detected shape and position. Name, colour, boulders and links stay.", outcome.Holds);
        Assert.Contains("1 hand-placed hold without an outline gets the detected outline", outcome.Holds);
        Assert.Contains("3 new holds are added with a suggested colour.", outcome.Holds);
        Assert.Contains("7 pairs of the same hold recorded twice are merged into one (red, “Jug”, no colour, red, red and 2 more).", outcome.Holds);
        Assert.Contains("1 detected hold that was not found again and that no boulder uses is removed.", outcome.Holds);
        Assert.Contains("4 shapes drawn or placed by hand stay untouched.", outcome.Boulders);
        Assert.Contains("2 more pairs that look alike are not merged and left for Wall settings › Possible duplicate holds.", outcome.Boulders);
        Assert.StartsWith("Each merge can be undone under Wall settings › Possible duplicate holds › Recent merges.", outcome.Undo);
        Assert.True(outcome.Danger);
        Assert.Equal("1 hold gets the newly detected shape and position. Name, colour, boulders and links stay.", harmless.Holds);
        Assert.Equal("This cannot be undone here.", harmless.Undo);
        Assert.False(harmless.Danger);
    }

    [Fact]
    public void AfterwardsAndWhenNothingChanges_OneLineSaysSo()
    {
        var done = new PanelRedetectSummary(9, 2, 1, 1, [new RedetectMergeInfo(Guid.NewGuid(), Guid.NewGuid(), null, "blue", 1)], 0, 2, 0, 0, FailedMerges: 1);

        Assert.Equal("Re-detect: 3 shapes updated, 1 new hold, 1 pair merged, 2 holds removed, 1 merge not done", RedetectOutcomes.Done(done));
        Assert.Equal("Re-detect found 9 holds, all of them already there. Nothing was changed.", RedetectOutcomes.NothingToDo(done with { Detected = 9 }));
        Assert.Equal("Re-detect found no holds on this photo. Nothing was changed.", RedetectOutcomes.NothingToDo(PanelRedetectSummary.Nothing));
    }

    [Theory]
    [InlineData("red", "red", HoldColorRelation.Same)]
    [InlineData("Red", " red", HoldColorRelation.Same)]
    [InlineData("red", null, HoldColorRelation.Unknown)]
    [InlineData("", "blue", HoldColorRelation.Unknown)]
    [InlineData("pink", "red", HoldColorRelation.Unknown)]
    [InlineData("purple", "blue", HoldColorRelation.Unknown)]
    [InlineData("wood-light", "orange", HoldColorRelation.Unknown)]
    [InlineData("gray", "white", HoldColorRelation.Unknown)]
    [InlineData("blue", "yellow", HoldColorRelation.Different)]
    [InlineData("white", "black", HoldColorRelation.Different)]
    [InlineData("green", "red", HoldColorRelation.Different)]
    public void Colours_AgreeDisagreeOrSayNothing(string? a, string? b, HoldColorRelation expected)
    {
        Assert.Equal(expected, HoldColorAgreement.Compare(a, b));
    }
}
