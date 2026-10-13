// <copyright file="RefreshFlowTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Web.Components.Shared;
using Blocwerk.Web.Components.Shared.Refresh;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// What the update page says above its screens: the header's step for each state of a run, the one status line, the
/// check's headline numbers and the order of its cards, what Apply says before it applies, and how the card deck
/// keeps its place while cards are answered.
/// </summary>
public class RefreshFlowTests
{
    [Theory]
    [InlineData(null, UpdateStep.Photos)]
    [InlineData(WallRefreshStatus.Uploading, UpdateStep.Photos)]
    [InlineData(WallRefreshStatus.Sorting, UpdateStep.Photos)]
    [InlineData(WallRefreshStatus.ReadyToStart, UpdateStep.Photos)]
    [InlineData(WallRefreshStatus.Running, UpdateStep.Check)]
    [InlineData(WallRefreshStatus.ReadyToApply, UpdateStep.Check)]
    [InlineData(WallRefreshStatus.Failed, UpdateStep.Check)]
    [InlineData(WallRefreshStatus.Applying, UpdateStep.Apply)]
    [InlineData(WallRefreshStatus.Done, UpdateStep.Apply)]
    public void EveryStateOfARun_HasItsStepInTheHeader(WallRefreshStatus? status, UpdateStep step) =>
        Assert.Equal(step, RefreshFlow.StepOf(status));

    [Fact]
    public void TheStatusLine_NamesWhatIsRunning_AndIsSilentWhenNothingIs()
    {
        var steps = RefreshTimeline.Initial().ToList();
        Assert.Null(RefreshFlow.StatusLine(View(WallRefreshStatus.Uploading, steps)));

        steps[0] = steps[0] with { State = RefreshStepState.Done };
        steps[3] = steps[3] with { State = RefreshStepState.Running, Detail = "Panel 2 of 3" };
        Assert.Equal("Holds detected on the new panel photos: Panel 2 of 3", RefreshFlow.StatusLine(View(WallRefreshStatus.Running, steps)));

        steps[3] = steps[3] with { State = RefreshStepState.Done };
        Assert.Equal("Working…", RefreshFlow.StatusLine(View(WallRefreshStatus.Running, steps)));
        Assert.Null(RefreshFlow.StatusLine(View(WallRefreshStatus.ReadyToApply, steps)));
        Assert.StartsWith("Updating the summary", RefreshFlow.StatusLine(View(WallRefreshStatus.ReadyToApply, steps) with { SummaryUpdating = true }));
        Assert.StartsWith("The new 3D model is ready", RefreshFlow.StatusLine(View(WallRefreshStatus.ReadyToApply, steps) with { Check3DPending = true }));
    }

    [Fact]
    public void TheNumbers_AreTheFiveOfAnUpdate_OrTheThreeOfStartingOver()
    {
        var summary = new RefreshSummary(212, 9, 0, 17, 4, 0, 3, 0, 6, ["Centre panel"], ReplacedHolds: 3);

        Assert.Equal(
            [(212, "found again", false), (17, "new", false), (9, "not found", true), (3, "replaced", true), (6, "boulders affected", true)],
            RefreshFlow.Numbers(summary).Select(n => (n.Value, n.Label, n.Attention)));
        Assert.All(RefreshFlow.Numbers(summary with { KeptInPlace = 0, ReplacedHolds = 0, BouldersOnKeptHolds = 0 }), n => Assert.False(n.Attention));

        var over = summary with { FullRevision = new RefreshFullRevision([new("Centre panel", 40)], 38, 51, ["Crimp line", "Warm-up"]) };
        Assert.Equal([(38, "new holds"), (51, "old holds retired"), (2, "boulders archived")], RefreshFlow.Numbers(over).Select(n => (n.Value, n.Label)));
    }

    [Fact]
    public void TheDeck_AsksAboutOpenCardsOnly_WhatBouldersUseFirst_ThenByWhatIsAtStake()
    {
        var unsure = Check(UpdateExceptionKind.LowConfidenceMatch);
        var removed = Check(UpdateExceptionKind.PossiblyRemoved);
        var replaced = Check(UpdateExceptionKind.ReplacedByOther);
        var unsureOnTwo = Check(UpdateExceptionKind.LowConfidenceMatch, boulders: 2);
        var removedOnOne = Check(UpdateExceptionKind.PossiblyRemoved, boulders: 1);
        var answered = Check(UpdateExceptionKind.ReplacedByOther, boulders: 5) with { Status = UpdateExceptionStatus.Kept };

        var queue = RefreshFlow.Queue([unsure, removed, answered, replaced, unsureOnTwo, removedOnOne]);

        Assert.Equal([removedOnOne.Id, unsureOnTwo.Id, replaced.Id, removed.Id, unsure.Id], queue.Select(c => c.Id));
    }

    [Fact]
    public void Apply_SaysHowManyCardsAreOpen_AndWhatBecomesOfThem()
    {
        var summary = new RefreshSummary(10, 0, 0, 2, 0, 0, 0, 0, 0, ["Centre panel"], ChecksOpen: 9);

        Assert.Equal("Apply update — 9 open", RefreshFlow.ApplyLabel(9, fullRevision: false));
        Assert.Equal("Apply update", RefreshFlow.ApplyLabel(0, fullRevision: false));
        Assert.Equal("Apply: start the wall over", RefreshFlow.ApplyLabel(0, fullRevision: true));
        Assert.Contains("9 cards are still open: their holds stay, marked for review", RefreshFlow.ApplyQuestion(summary));
        Assert.Contains("Every card is answered.", RefreshFlow.ApplyQuestion(summary with { ChecksOpen = 0 }));
        Assert.EndsWith("this cannot be undone from the app.", RefreshFlow.ApplyQuestion(summary));

        var over = summary with { FullRevision = new RefreshFullRevision([], 38, 51, ["Crimp line"]) };
        Assert.Equal("Start the wall over now? 51 old holds are retired, 38 new holds are created and 1 boulder is archived. This cannot be undone from the app.", RefreshFlow.ApplyQuestion(over));
    }

    [Fact]
    public void ACardsAnswers_AreTheListsAnswers()
    {
        var unsure = Check(UpdateExceptionKind.LowConfidenceMatch);
        var removed = Check(UpdateExceptionKind.PossiblyRemoved);

        Assert.False(RefreshCheckCopy.TakesRemove(unsure));
        Assert.True(RefreshCheckCopy.TakesRemove(removed));
        Assert.False(RefreshCheckCopy.TakesRemove(removed with { CanRemove = false }));
        Assert.Equal("Match is right", RefreshCheckCopy.KeepLabel(unsure));
        Assert.Equal("Remove hold", RefreshCheckCopy.AnsweredLabel(removed with { Status = UpdateExceptionStatus.Removed }));
        Assert.Equal("Keep hold", RefreshCheckCopy.AnsweredLabel(removed with { Status = UpdateExceptionStatus.Kept }));
    }

    [Fact]
    public void TheDeck_ShowsTheNextCardAfterAnAnswer_AndReturnsToACardWhoseAnswerIsTakenBack()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var cursor = new DeckCursor();
        cursor.Sync([a, b, c]);
        Assert.Equal(a, cursor.Current);

        cursor.Step(1);
        Assert.Equal((b, 1), (cursor.Current, cursor.Index));

        // b is answered and leaves: c takes its place, without a tap.
        cursor.Sync([a, c]);
        Assert.Equal((c, 1), (cursor.Current, cursor.Index));

        // The last card is answered: the deck stays on what is now the last one.
        cursor.Sync([a]);
        Assert.Equal(a, cursor.Current);

        // b's answer is taken back: it is shown again once it is in the queue.
        cursor.ReturnTo(b);
        cursor.Sync([a, b]);
        Assert.Equal(b, cursor.Current);
        cursor.Sync([a, b]);
        Assert.Equal(b, cursor.Current);
    }

    [Fact]
    public void TheDeck_TurnsAroundTheEnds_JumpsToACard_AndStartsOverOnAnotherQueue()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var cursor = new DeckCursor();
        cursor.Step(1);
        Assert.Null(cursor.Current);

        cursor.Sync([a, b, c]);
        cursor.Step(-1);
        Assert.Equal(c, cursor.Current);
        cursor.Step(1);
        Assert.Equal(a, cursor.Current);

        cursor.JumpTo(c);
        cursor.JumpTo(Guid.NewGuid());
        Assert.Equal(c, cursor.Current);

        cursor.Reset();
        cursor.Sync([b, c]);
        Assert.Equal(b, cursor.Current);
    }

    private static RefreshCheck Check(UpdateExceptionKind kind, int boulders = 0) =>
        new(Guid.NewGuid(), kind, UpdateExceptionStatus.Pending, true, false, boulders, "yellow", 0.4);

    private static WallRefreshView View(WallRefreshStatus status, IReadOnlyList<RefreshStep> steps) =>
        new(Guid.NewGuid(), Guid.NewGuid(), status, [], [], [], [.. steps], null, null, false, null, DateTimeOffset.UtcNow);
}
