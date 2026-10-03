// <copyright file="CaptureFollowUpModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>When the chain runs.</summary>
public enum CaptureFollowUpPhase
{
    /// <summary>Right after the model and its textures are live (the photo-real view may still be training).</summary>
    Model = 0,

    /// <summary>At the end of the capture, with or without a photo-real view.</summary>
    Final = 1,

    /// <summary>
    /// After the capture completed (model ready, photo-real view stored or waiting for a runner): slow steps that
    /// only propose, so they never hold up the capture's done state or the other steps' results.
    /// </summary>
    AfterCompletion = 2,
}

/// <summary>What a recorded step did. Stored by name; never rename.</summary>
public enum CaptureFollowUpOutcome
{
    /// <summary>It ran; its summary says what changed.</summary>
    Done = 0,

    /// <summary>Nothing to do here (no unplaced holds, no photo-real view, not available on this server).</summary>
    Skipped = 1,

    /// <summary>It threw; the other steps still ran.</summary>
    Failed = 2,
}

/// <summary>The capture the chain runs for.</summary>
/// <param name="CaptureId">The capture.</param>
/// <param name="WallId">Its wall.</param>
/// <param name="ModelId">The model it produced (the wall's active one, or the chain does not run).</param>
/// <param name="ActingUserId">The admin who started it.</param>
/// <param name="SplatId">The model's photo-real view, when it has one.</param>
public sealed record CaptureFollowUpContext(Guid CaptureId, Guid WallId, Guid ModelId, Guid ActingUserId, Guid? SplatId);

/// <summary>A step's outcome with its one-line, plain-words summary (empty when there is nothing worth saying).</summary>
/// <param name="Outcome">Done or skipped (failures are recorded by the chain).</param>
/// <param name="Summary">E.g. "856 holds placed on the 3D model"; for a skipped step, why (logged, not shown).</param>
public sealed record CaptureFollowUpStepResult(CaptureFollowUpOutcome Outcome, string Summary)
{
    /// <summary>The step ran.</summary>
    /// <param name="summary">What changed.</param>
    /// <returns>The result.</returns>
    public static CaptureFollowUpStepResult Done(string summary) => new(CaptureFollowUpOutcome.Done, summary);

    /// <summary>The step had nothing to do.</summary>
    /// <param name="reason">Why (logged, not shown).</param>
    /// <returns>The result.</returns>
    public static CaptureFollowUpStepResult Skipped(string reason) => new(CaptureFollowUpOutcome.Skipped, reason);
}
