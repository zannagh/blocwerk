// <copyright file="WallUpdateShapeModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>How to run the optional shape recognition of a wall update.</summary>
/// <param name="Scope">Which staged holds to outline.</param>
/// <param name="OverwriteManual">Also re-outline holds whose shape a person drew by hand. Default false.</param>
/// <param name="Rerun">Throw away every earlier proposal (and its review verdict) and start over.</param>
public sealed record ShapeRecognitionOptions(
    ShapeRecognitionScope Scope = ShapeRecognitionScope.NewAndChanged,
    bool OverwriteManual = false,
    bool Rerun = false);

/// <summary>The shape step's run state plus the review tally, as the UI and the API poll it.</summary>
/// <param name="SessionId">The wall update session the run belongs to.</param>
/// <param name="Phase">The session's resume cursor — where the wizard reopens, whoever drove the step.</param>
/// <param name="Available">Whether outline detection is available on this server at all.</param>
/// <param name="Status">Where the step stands.</param>
/// <param name="Interrupted">True when the status says Running but no run is alive (the app restarted): start again to resume.</param>
/// <param name="Scope">The scope of the latest run.</param>
/// <param name="OverwriteManual">Whether the latest run could replace hand-drawn outlines.</param>
/// <param name="Total">Holds to outline.</param>
/// <param name="Done">Holds outlined so far.</param>
/// <param name="SkippedManual">Holds left alone because a person drew their outline.</param>
/// <param name="StartedAt">When the latest run started.</param>
/// <param name="FinishedAt">When it finished, failed or was skipped.</param>
/// <param name="Error">Why it failed, if it did.</param>
/// <param name="Tally">Proposals per review verdict.</param>
public sealed record ShapeRecognitionStatusInfo(
    Guid SessionId,
    WallUpdatePhase Phase,
    bool Available,
    ShapeRecognitionStatus Status,
    bool Interrupted,
    ShapeRecognitionScope Scope,
    bool OverwriteManual,
    int Total,
    int Done,
    int SkippedManual,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    IReadOnlyDictionary<ShapeReviewDecision, int> Tally);

/// <summary>One recognised shape to review. Shapes are in the <see cref="Hold.ShapePoints"/> convention, relative to (X, Y).</summary>
/// <param name="HoldId">The staged hold.</param>
/// <param name="PanelId">The staged panel whose photo was outlined.</param>
/// <param name="PanelCol">That panel's grid column.</param>
/// <param name="PanelRow">That panel's grid row.</param>
/// <param name="X">The hold centre (normalized) the shapes are relative to.</param>
/// <param name="Y">The hold centre Y.</param>
/// <param name="Radius">The hold's radius (normalized by the photo's longer side).</param>
/// <param name="Reason">Why the hold was in scope.</param>
/// <param name="Method">How the outline was found.</param>
/// <param name="Confidence">0..1; circle fallbacks stay at or below 0.2.</param>
/// <param name="ImageWidth">Photo width in pixels.</param>
/// <param name="ImageHeight">Photo height in pixels.</param>
/// <param name="Shape">The recognised outline, or null for a circle fallback.</param>
/// <param name="Holes">Recognised interior holes, or null.</param>
/// <param name="PreviousShape">The shape the hold promotes with when the step does nothing, or null (a circle).</param>
/// <param name="Decision">The review verdict so far.</param>
/// <param name="AdjustedShape">The reviewer's own outline, for <see cref="ShapeReviewDecision.Adjusted"/>.</param>
public sealed record ShapeProposalInfo(
    Guid HoldId,
    Guid PanelId,
    int PanelCol,
    int PanelRow,
    double X,
    double Y,
    double Radius,
    ShapeProposalReason Reason,
    HoldOutlineMethod Method,
    double Confidence,
    int ImageWidth,
    int ImageHeight,
    List<ShapePoint>? Shape,
    List<List<ShapePoint>>? Holes,
    List<ShapePoint>? PreviousShape,
    ShapeReviewDecision Decision,
    List<ShapePoint>? AdjustedShape);

/// <summary>One review verdict.</summary>
/// <param name="HoldId">The staged hold.</param>
/// <param name="Decision">The verdict.</param>
/// <param name="Shape">For <see cref="ShapeReviewDecision.Adjusted"/>: the outline, ≥ 3 points relative to the proposal's (X, Y).</param>
public sealed record ShapeDecisionRequest(Guid HoldId, ShapeReviewDecision Decision, List<ShapePoint>? Shape = null);
