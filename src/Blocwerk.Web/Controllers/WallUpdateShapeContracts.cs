// <copyright file="WallUpdateShapeContracts.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Controllers;

/// <summary>A point of an outline, relative to the hold centre, as fractions of the photo width/height.</summary>
public record ShapePointDto(double Dx, double Dy);

/// <summary>Starts the shape recognition. Enum values are their names, case-insensitive.</summary>
/// <param name="Scope">"NewAndChanged" (default), "New", "Changed" or "All".</param>
/// <param name="OverwriteManual">Also re-outline hand-drawn shapes. Default false.</param>
/// <param name="Rerun">Drop earlier proposals and verdicts and start over.</param>
/// <param name="SessionId">The update session the caller is working on; refused (409) when it is no longer the open one.</param>
public record ShapeRecognitionStartRequest(string? Scope = null, bool OverwriteManual = false, bool Rerun = false, Guid? SessionId = null);

/// <summary>Names the update session a write is meant for (the stale-client guard).</summary>
public record ShapeSessionRequest(Guid? SessionId = null);

/// <summary>Accepts every pending recognised outline at or above a confidence.</summary>
public record ShapeAcceptAboveRequest(double MinConfidence, Guid? SessionId = null);

/// <summary>One verdict: "Accepted", "Adjusted" (with <paramref name="Shape"/>), "Circle", "KeepPrevious" or "Pending".</summary>
public record ShapeDecisionDto(Guid HoldId, string Decision, List<ShapePointDto>? Shape = null);

/// <summary>A batch of verdicts.</summary>
public record ShapeDecisionsRequest(List<ShapeDecisionDto> Decisions, Guid? SessionId = null);

/// <summary>How many proposals a write changed.</summary>
public record ShapeWriteResponse(int Count);

/// <summary>The recognition run and review tally.</summary>
public record ShapeRecognitionStatusResponse(
    Guid SessionId,
    bool Available,
    string Status,
    bool Interrupted,
    string Scope,
    bool OverwriteManual,
    int Total,
    int Done,
    int SkippedManual,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    Dictionary<string, int> Decisions);

/// <summary>One recognised shape. Shapes are relative to (X, Y).</summary>
public record ShapeProposalResponse(
    Guid HoldId,
    Guid PanelId,
    int PanelCol,
    int PanelRow,
    double X,
    double Y,
    double Radius,
    string Reason,
    string Method,
    double Confidence,
    int ImageWidth,
    int ImageHeight,
    List<ShapePointDto>? Shape,
    List<ShapePointDto>? PreviousShape,
    string Decision,
    List<ShapePointDto>? AdjustedShape);

/// <summary>Maps the Core shape-step projections onto the wire contracts above.</summary>
internal static class WallUpdateShapeMappings
{
    public static ShapeRecognitionStatusResponse ToResponse(this ShapeRecognitionStatusInfo s) =>
        new(
            s.SessionId,
            s.Available,
            s.Status.ToString(),
            s.Interrupted,
            s.Scope.ToString(),
            s.OverwriteManual,
            s.Total,
            s.Done,
            s.SkippedManual,
            s.StartedAt,
            s.FinishedAt,
            s.Error,
            s.Tally.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value));

    public static ShapeProposalResponse ToResponse(this ShapeProposalInfo p) =>
        new(
            p.HoldId,
            p.PanelId,
            p.PanelCol,
            p.PanelRow,
            p.X,
            p.Y,
            p.Radius,
            p.Reason.ToString(),
            p.Method.ToString(),
            p.Confidence,
            p.ImageWidth,
            p.ImageHeight,
            ToDto(p.Shape),
            ToDto(p.PreviousShape),
            p.Decision.ToString(),
            ToDto(p.AdjustedShape));

    public static List<ShapePoint>? ToShape(List<ShapePointDto>? points) =>
        points?.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList();

    private static List<ShapePointDto>? ToDto(List<ShapePoint>? shape) =>
        shape?.Select(p => new ShapePointDto(p.Dx, p.Dy)).ToList();
}
