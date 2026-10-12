// <copyright file="HoldColorProposalMarkTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.HoldColors;

/// <summary>
/// A proposed colour stays marked as a proposal until a person keeps or changes it, and never outranks a colour a
/// person set: in the hold editor, across linked holds and when holds are merged.
/// </summary>
public class HoldColorProposalMarkTests
{
    [Fact]
    public async Task SavingTheHoldFromTheEditor_KeepsTheMark_UntilThePersonKeepsOrChangesTheColour()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0);
        var moved = await s.AddHoldAsync(panel, HoldColorScenario.BlueSpot, configure: Propose("blue"));
        var kept = await s.AddHoldAsync(panel, HoldColorScenario.RedSpot, configure: Propose("red"));
        var changed = await s.AddHoldAsync(panel, HoldColorScenario.GreenSpot, configure: Propose("green"));

        // The editor sends the whole hold on every save; only "Keep" or another colour is a person's word on it.
        await EditAsync(s, moved, h => Whole(h, h.Color, stillProposed: true) with { X = 0.21 });
        await EditAsync(s, kept, h => Whole(h, h.Color, stillProposed: false));
        await EditAsync(s, changed, h => Whole(h, "yellow", stillProposed: false));

        Assert.Equal(("blue", true), Mark(await s.GetAsync(moved)));
        Assert.Equal(("red", false), Mark(await s.GetAsync(kept)));
        Assert.Equal(("yellow", false), Mark(await s.GetAsync(changed)));
        Assert.Null((await s.GetAsync(kept)).ProposedColor);
    }

    [Fact]
    public async Task KeepingAProposal_OnOnePanel_KeepsItOnTheLinkedHoldToo()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var a = await s.AddHoldAsync(await s.AddPanelAsync(0), HoldColorScenario.BlueSpot, configure: Propose("blue"));
        var b = await s.AddHoldAsync(await s.AddPanelAsync(1), HoldColorScenario.BlueSpot, configure: Propose("blue"));
        await s.LinkAsync(a, b);

        await EditAsync(s, a, h => Whole(h, h.Color, stillProposed: false));

        Assert.Equal(("blue", false), Mark(await s.GetAsync(a)));
        Assert.Equal(("blue", false), Mark(await s.GetAsync(b)));
    }

    [Fact]
    public void AcrossLinkedHolds_AColourAPersonSetBeatsAProposal_WhateverThePanelOrder()
    {
        var proposedOnWinnerPanel = Proposed("blue");
        var setByHand = new Hold { Color = "purple" };
        var report = new HoldSyncReport();

        LinkedHoldSyncRules.Apply([proposedOnWinnerPanel, setByHand], report);

        Assert.Equal(("purple", false), Mark(proposedOnWinnerPanel));
        Assert.Equal(("purple", false), Mark(setByHand));
        Assert.Equal(1, report.UpdatedByProperty["Color"]);
    }

    [Fact]
    public void AcrossLinkedHolds_AProposalFillsABlankTwin_AndStaysAProposalOnBoth()
    {
        var proposed = Proposed("blue");
        var blank = new Hold();
        var otherProposal = Proposed("green");

        LinkedHoldSyncRules.Apply([blank, proposed, otherProposal], new HoldSyncReport());

        Assert.All(new[] { blank, proposed, otherProposal }, h => Assert.Equal(("blue", true), Mark(h)));
    }

    [Fact]
    public void GapFillsAndMerges_CarryTheMarkWithTheColour()
    {
        var filled = new Hold();
        var component = new List<Hold> { new(), Proposed("red") };
        var keptBlank = new Hold();
        var keptSet = new Hold { Color = "yellow" };

        HoldPropertySync.FillMissingAppearance(Proposed("blue"), filled);
        HoldPropertySync.FillGapsAcrossComponent(component);
        Detection.Outlines.HoldMergeRules.MergeFlags(keptBlank, Proposed("green"));
        Detection.Outlines.HoldMergeRules.MergeFlags(keptSet, Proposed("green"));

        Assert.Equal(("blue", true), Mark(filled));
        Assert.Equal(("red", true), Mark(component[0]));
        Assert.Equal(("green", true), Mark(keptBlank));
        Assert.Equal(("yellow", false), Mark(keptSet));
        Assert.Equal(("blue", true), Mark(Proposed("blue").Clone()));
    }

    [Theory]
    [InlineData(null, false, "blue", false)]
    [InlineData("green", true, "blue", false)]
    [InlineData("green", false, "green", false)]
    public void MergingAHoldSetByAPerson_IntoABlankProposedOrSetKeeper_KeepsAPersonsColour(
        string? keptColor, bool keptProposed, string expected, bool expectedProposed)
    {
        // "It is this hold": the old hold (blue, set by a person) is merged into a keeper in each colour state.
        var kept = new Hold { Color = keptColor, ProposedColor = keptProposed ? keptColor : null };

        Detection.Outlines.HoldMergeRules.MergeFlags(kept, new Hold { Color = "blue" });

        Assert.Equal((expected, expectedProposed), Mark(kept));
        Assert.Null(kept.ProposedColor);
    }

    [Fact]
    public void MergingAProposal_NeverReplacesWhatTheKeeperHas()
    {
        var keptProposal = Proposed("green");
        var keptSet = new Hold { Color = "yellow" };

        Detection.Outlines.HoldMergeRules.MergeFlags(keptProposal, Proposed("blue"));
        Detection.Outlines.HoldMergeRules.MergeFlags(keptSet, Proposed("blue"));

        Assert.Equal(("green", true), Mark(keptProposal));
        Assert.Equal(("yellow", false), Mark(keptSet));
    }

    [Fact]
    public void AMarkLeftFromAnEarlierProposal_DoesNotComeBack_WhenTheHoldIsLaterSetToThatColour()
    {
        // Each hold was proposed red once and then set to blue by a path that left the old proposal behind.
        Hold Stale() => new() { Color = "blue", ProposedColor = "red" };
        var viaSync = Stale();
        var viaEdit = Stale();
        var viaCopy = Stale();
        var before = new Hold { Color = "blue" };
        var edited = new Hold { Color = "red" };

        LinkedHoldSyncRules.Apply([new Hold { Color = "red" }, viaSync], new HoldSyncReport());
        HoldPropertySync.CopyChangedAppearance(before, edited, viaEdit);
        HoldPropertySync.CopyAppearance(new Hold { Color = "red" }, viaCopy);

        Assert.All(new[] { viaSync, viaEdit, viaCopy }, h => Assert.Equal(("red", false), Mark(h)));
        Assert.All(new[] { viaSync, viaEdit, viaCopy }, h => Assert.Null(h.ProposedColor));
    }

    [Fact]
    public void LinkedSync_DropsAStaleMark_AndCopyingAProposalMarksATwinThatAlreadyHasTheColour()
    {
        var stale = new Hold { Color = "blue", ProposedColor = "red" };
        var twin = new Hold { Color = "blue" };
        var sameColour = new Hold { Color = "green" };
        var report = new HoldSyncReport();

        LinkedHoldSyncRules.Apply([twin, stale], report);
        var changed = HoldPropertySync.CopyAppearance(Proposed("green"), sameColour);

        Assert.Null(stale.ProposedColor);
        Assert.False(report.Any);
        Assert.True(changed);
        Assert.Equal(("green", true), Mark(sameColour));
    }

    private static (string? Color, bool Proposed) Mark(Hold hold) => (hold.Color, hold.HasProposedColor);

    private static Hold Proposed(string color) => new() { Color = color, ProposedColor = color };

    private static Action<Hold> Propose(string color) => h => (h.Color, h.ProposedColor) = (color, color);

    private static HoldEdit Whole(Hold hold, string? color, bool stillProposed) => HoldEdit.FromEditorState(
        hold.X, hold.Y, hold.Radius, color, hold.Category, hold.IsOnKickboard, hold.ShapePoints, hold.Name, hold.Material, hold.HandType,
        flagBouldersOnMove: false, colorStillProposed: stillProposed);

    private static async Task EditAsync(HoldColorScenario s, Guid holdId, Func<Hold, HoldEdit> edit)
    {
        var service = new WallService(
            s.Harness.DbContextFactory, s.Harness.CurrentUser, s.Harness.HoldDetection, s.Harness.ActivityLog, NullLogger<WallService>.Instance);
        await service.UpdateHoldAsync(holdId, edit(await s.GetAsync(holdId)));
    }
}
