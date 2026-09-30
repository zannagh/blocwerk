// <copyright file="RefreshModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Refresh;

/// <summary>How sure the sorter is that a photo is the new photo of a panel.</summary>
public enum PanelPickConfidence
{
    /// <summary>No photo matches well enough: the panel keeps its current photo unless the user picks one.</summary>
    None = 0,

    /// <summary>Likely, but a close runner-up or partial coverage: shown as "Check this match".</summary>
    Medium = 1,

    /// <summary>Covers the panel, frontal, clearly better than any other photo.</summary>
    High = 2,
}

/// <summary>One uploaded photo's fit for one panel.</summary>
/// <param name="PhotoId">The capture photo.</param>
/// <param name="Score">0..1, higher is better.</param>
/// <param name="Coverage">Share of the current panel photo the photo shows (0..1).</param>
/// <param name="Frontal">1 when it is taken from the same angle as the current photo, towards 0 when skewed.</param>
public sealed record PanelPhotoCandidate(Guid PhotoId, double Score, double Coverage, double Frontal);

/// <summary>The photo chosen for one panel, with the runners-up.</summary>
public sealed record PanelPick(
    int Col,
    int Row,
    Guid? PhotoId,
    PanelPickConfidence Confidence,
    IReadOnlyList<PanelPhotoCandidate> Candidates,
    Guid? PanelId = null);

/// <summary>What happened to one timeline step.</summary>
public enum RefreshStepState
{
    Pending = 0,
    Running = 1,
    Done = 2,
    Skipped = 3,
    Failed = 4,
    Waiting = 5,
}

/// <summary>One line of the progress timeline.</summary>
public sealed record RefreshStep(string Key, string Title, RefreshStepState State, string? Detail = null);

/// <summary>An uploaded video, kept until it is handed to the capture.</summary>
public sealed record RefreshVideo(string StoredName, string? FileName, long SizeBytes);

/// <summary>What the confirm screen shows before the panel update is applied.</summary>
/// <param name="Refound">Old holds found again on the new photos.</param>
/// <param name="KeptInPlace">Old holds not found again, kept where they were.</param>
/// <param name="PossiblyMoved">"This hold may have moved" suggestions, not applied in the quick review.</param>
/// <param name="NewHolds">New holds that will be added.</param>
/// <param name="DroppedDetections">Detections left out (on a marker, outside the old photo, unchanged).</param>
/// <param name="Removed">Old holds that will be removed.</param>
/// <param name="OverlapLinks">Holds linked across neighbouring panels (matches of 90 % or more).</param>
/// <param name="OverlapsLeftOut">Overlap suggestions below 90 %, left unlinked.</param>
/// <param name="BouldersOnKeptHolds">Boulders using a hold that was not found again.</param>
/// <param name="Panels">The panels that get a new photo, named for people ("Centre panel", "Right panel").</param>
public sealed record RefreshSummary(
    int Refound,
    int KeptInPlace,
    int PossiblyMoved,
    int NewHolds,
    int DroppedDetections,
    int Removed,
    int OverlapLinks,
    int OverlapsLeftOut,
    int BouldersOnKeptHolds,
    IReadOnlyList<string> Panels);

/// <summary>An uploaded file, as the drop zone lists it.</summary>
public sealed record RefreshFile(Guid? PhotoId, string? FileName, bool IsVideo, string? Problem);

/// <summary>Everything the "Update panels + 3D" page shows.</summary>
public sealed record WallRefreshView(
    Guid Id,
    Guid WallId,
    WallRefreshStatus Status,
    IReadOnlyList<CapturePhotoResult> Photos,
    IReadOnlyList<RefreshVideo> Videos,
    IReadOnlyList<PanelPick> Picks,
    IReadOnlyList<RefreshStep> Steps,
    RefreshSummary? Summary,
    WallCaptureSummary? Capture,
    bool CaptureAvailable,
    string? Error,
    DateTimeOffset CreatedAt,
    Guid? CaptureId = null,
    Guid? UpdateSessionId = null)
{
    public bool IsWorking => Status is WallRefreshStatus.Sorting or WallRefreshStatus.Running or WallRefreshStatus.Applying;
}
