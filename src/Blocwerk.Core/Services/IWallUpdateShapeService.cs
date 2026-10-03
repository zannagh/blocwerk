// <copyright file="IWallUpdateShapeService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>
/// The optional "recognise hold shapes" step of a wall update and the review after it. The run outlines
/// the update's STAGED holds on their staged photos and records one proposal per hold with its confidence;
/// the review decides per hold (accept, adjust, circle, keep the previous shape). Nothing reaches a hold
/// until <see cref="IWallBigUpdateService.PromoteAsync"/>, which applies the verdicts of a completed run.
/// <para>
/// Every method is gated like the rest of the update: wall admin (<see cref="WallAdminGuard"/>), never from
/// a kiosk, and every write takes the caller's session id so a stale tab cannot act on a newer update
/// (<see cref="WallUpdateSessionSupersededException"/>). Works on every wall, markers or not.
/// </para>
/// </summary>
public interface IWallUpdateShapeService
{
    /// <summary>
    /// Starts (or resumes) the recognition in the background and returns at once; poll
    /// <see cref="GetStatusAsync"/>. A run already alive is left alone. Holds whose outline a person drew are
    /// skipped unless <see cref="ShapeRecognitionOptions.OverwriteManual"/>.
    /// </summary>
    Task<ShapeRecognitionStatusInfo> StartRecognitionAsync(
        Guid wallId, ShapeRecognitionOptions options, Guid? expectedSessionId = null, CancellationToken ct = default);

    /// <summary>The run state of the wall's open update. Throws when no update is open.</summary>
    Task<ShapeRecognitionStatusInfo> GetStatusAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Skips the step (stopping a live run): every hold promotes with the shape it has now.</summary>
    Task<ShapeRecognitionStatusInfo> SkipAsync(Guid wallId, Guid? expectedSessionId = null, CancellationToken ct = default);

    /// <summary>
    /// Declares the shape step finished (review done, or the run skipped) and moves the update on to its
    /// confirm step. Every method here also moves the session's resume cursor (start → Shapes, run complete or
    /// a verdict → ShapeReview, skip or this → Confirm), so an API-driven update reopens in the wizard where the
    /// API left it.
    /// </summary>
    Task<ShapeRecognitionStatusInfo> CompleteReviewAsync(Guid wallId, Guid? expectedSessionId = null, CancellationToken ct = default);

    /// <summary>The recognised shapes, lowest confidence first, optionally only those below a confidence.</summary>
    Task<IReadOnlyList<ShapeProposalInfo>> GetProposalsAsync(
        Guid wallId, double? belowConfidence = null, CancellationToken ct = default);

    /// <summary>Records review verdicts. Refused unless the run completed; unknown holds are refused as a whole.</summary>
    /// <returns>The number of proposals written.</returns>
    Task<int> DecideAsync(
        Guid wallId, IReadOnlyList<ShapeDecisionRequest> decisions, Guid? expectedSessionId = null, CancellationToken ct = default);

    /// <summary>Accepts every still-pending recognised outline at or above <paramref name="minConfidence"/>.</summary>
    /// <returns>The number of proposals accepted.</returns>
    Task<int> AcceptAboveAsync(
        Guid wallId, double minConfidence, Guid? expectedSessionId = null, CancellationToken ct = default);
}
