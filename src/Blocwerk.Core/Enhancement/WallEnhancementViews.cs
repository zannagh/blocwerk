// <copyright file="WallEnhancementViews.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Enhancement;

/// <summary>Where the wall's current generation is with its enhancement.</summary>
public enum WallEnhancementState
{
    /// <summary>Not enhanced yet: the button starts it (unless <see cref="WallEnhancementView.BlockedReason"/>).</summary>
    NotStarted = 0,

    /// <summary>The steps run in the background.</summary>
    Running = 1,

    /// <summary>The steps are done; the review waits.</summary>
    Review = 2,

    /// <summary>Enhanced for this generation: the button does nothing.</summary>
    Finalized = 3,

    /// <summary>Stopped; starting again continues where it stopped.</summary>
    Failed = 4,
}

/// <summary>One step as the card shows it.</summary>
/// <param name="Key">The step key.</param>
/// <param name="Title">What it does.</param>
/// <param name="Outcome">How it went (null: not started yet).</param>
/// <param name="Summary">Its one-line summary.</param>
public sealed record WallEnhancementStepView(string Key, string Title, WallEnhancementStepOutcomeKind? Outcome, string Summary);

/// <summary>The review counts of one kind.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Pending">Still to answer.</param>
/// <param name="Accepted">Kept or carried out.</param>
/// <param name="Rejected">Undone or turned down.</param>
/// <param name="Skipped">Could not be acted on any more.</param>
public sealed record WallEnhancementKindCount(WallEnhancementItemKind Kind, int Pending, int Accepted, int Rejected, int Skipped)
{
    /// <summary>All items of the kind.</summary>
    public int Total => Pending + Accepted + Rejected + Skipped;
}

/// <summary>The enhancement of a wall's current generation, for the settings card and the review page.</summary>
/// <param name="State">Where it is.</param>
/// <param name="Generation">The wall's current generation.</param>
/// <param name="RunId">The run of this generation, if any.</param>
/// <param name="Steps">Every step in order, with what it did so far.</param>
/// <param name="CurrentStep">The step that runs now, if any.</param>
/// <param name="Counts">The review counts per kind (kinds with items only).</param>
/// <param name="StartedAt">When the run started.</param>
/// <param name="StartedBy">Who started it.</param>
/// <param name="FinalizedAt">When it was finalized.</param>
/// <param name="FinalizedBy">Who finalized it.</param>
/// <param name="BlockedReason">Why it cannot start now (an open wall update), or null.</param>
/// <param name="Error">Why it stopped, for a failed run.</param>
/// <param name="WaitingForEditor">True while the run waits for someone to close the wall's hold editor.</param>
/// <param name="PreviousFinalizedAt">When an earlier generation was enhanced, if one was.</param>
/// <param name="StepProgress">How far the running step is (photos done of all), when it reports it.</param>
/// <param name="StepExpectedEnd">When the running step is expected to be done, once its pace is known.</param>
/// <param name="Stalled">True while a Running run has shown no sign of life for a while (its job is gone or hung): cancel it.</param>
public sealed record WallEnhancementView(
    WallEnhancementState State,
    int Generation,
    Guid? RunId,
    IReadOnlyList<WallEnhancementStepView> Steps,
    WallEnhancementStepView? CurrentStep,
    IReadOnlyList<WallEnhancementKindCount> Counts,
    DateTimeOffset? StartedAt,
    string? StartedBy,
    DateTimeOffset? FinalizedAt,
    string? FinalizedBy,
    string? BlockedReason,
    string? Error,
    bool WaitingForEditor,
    DateTimeOffset? PreviousFinalizedAt,
    WallEnhancementProgress? StepProgress = null,
    DateTimeOffset? StepExpectedEnd = null,
    bool Stalled = false)
{
    /// <summary>Items still to answer, over all kinds.</summary>
    public int Pending => Counts.Sum(c => c.Pending);

    /// <summary>The number of the running step (1-based), or 0.</summary>
    public int StepNumber => CurrentStep is null ? 0 : Steps.ToList().FindIndex(s => s.Key == CurrentStep.Key) + 1;
}

/// <summary>One review item.</summary>
/// <param name="Id">The item.</param>
/// <param name="Kind">What it is about.</param>
/// <param name="HoldId">The hold (left of a pair).</param>
/// <param name="OtherHoldId">The other hold of a pair, or the base of a stack.</param>
/// <param name="ReferenceId">The hold proposal of a new hold.</param>
/// <param name="Detail">The suggested colour or merge mode.</param>
/// <param name="Decision">The answer so far.</param>
/// <param name="Note">Why an answer could not be carried out.</param>
/// <param name="HoldName">The hold's name, if it has one.</param>
/// <param name="HoldColor">The hold's colour now.</param>
/// <param name="X">Where the hold is across its photo (0..1), when known.</param>
/// <param name="Y">Where the hold is down its photo (0..1), when known.</param>
public sealed record WallEnhancementItemView(
    Guid Id,
    WallEnhancementItemKind Kind,
    Guid? HoldId,
    Guid? OtherHoldId,
    Guid? ReferenceId,
    string? Detail,
    WallEnhancementDecision Decision,
    string? Note,
    string? HoldName,
    string? HoldColor,
    double? X,
    double? Y);

/// <summary>A page of review items.</summary>
/// <param name="Total">Items of the kind (matching the filter).</param>
/// <param name="Items">This page.</param>
public sealed record WallEnhancementItemPage(int Total, IReadOnlyList<WallEnhancementItemView> Items);

/// <summary>What discarding a run took back.</summary>
/// <param name="HoldsRestored">Holds the steps wrote that are back as they were.</param>
/// <param name="AnswersTakenBack">Answers whose writes were taken back (merges, links, added holds, confirmed colours).</param>
/// <param name="LeftAsTheyAre">Holds or answers changed since by someone else, so left alone.</param>
/// <param name="RecordsRestored">Suggestion records put back (proposals pending again, link suggestions offered again, dismissals forgotten).</param>
public sealed record WallEnhancementDiscardResult(int HoldsRestored, int AnswersTakenBack, int LeftAsTheyAre, int RecordsRestored = 0);

/// <summary>What answering an item did.</summary>
/// <param name="Decision">The recorded answer (Skipped when it could not be carried out).</param>
/// <param name="Message">One line for people.</param>
public sealed record WallEnhancementDecisionResult(WallEnhancementDecision Decision, string Message);
