// <copyright file="ICaptureFollowUpStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// One step of the post-capture chain (<see cref="CaptureFollowUpChain"/>): something that makes the wall's
/// existing data better once a capture's model is live. A step only ever writes DERIVED 3D data (placements,
/// footprints, protrusion); panel hold positions and shapes and boulders are panel truth and stay review-gated.
/// </summary>
public interface ICaptureFollowUpStep
{
    /// <summary>Stable key recorded on the capture (never rename: a restart resumes by it).</summary>
    string Key { get; }

    /// <summary>Position in the chain (ascending).</summary>
    int Order { get; }

    /// <summary>What the step does, for the status line ("Placing the existing holds on the 3D model").</summary>
    string Title { get; }

    /// <summary>
    /// True when the step works on the photo-real view: it runs only in <see cref="CaptureFollowUpPhase.Final"/>
    /// and again whenever the model gets a different photo-real view (a retrain).
    /// </summary>
    bool NeedsPhotoReal { get; }

    /// <summary>
    /// True for a slow step that only proposes (nothing live changes): it runs in
    /// <see cref="CaptureFollowUpPhase.AfterCompletion"/>, once the capture already shows as done, and in no other
    /// phase.
    /// </summary>
    bool RunsAfterCompletion => false;

    /// <summary>
    /// A fingerprint of what an after-completion step reads besides the photos (e.g. the visible volumes): the
    /// recorded step runs again only when it changes. Null: it runs once.
    /// </summary>
    /// <param name="context">The capture and its model.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The fingerprint, or null.</returns>
    Task<string?> InputsKeyAsync(CaptureFollowUpContext context, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>Runs the step. Throwing records it as failed; the chain goes on with the next step.</summary>
    /// <param name="context">The capture and its model.</param>
    /// <param name="ct">Cancellation (app shutdown: nothing is recorded and the step runs again on resume).</param>
    /// <returns>The outcome and its one-line summary.</returns>
    Task<CaptureFollowUpStepResult> RunAsync(CaptureFollowUpContext context, CancellationToken ct);
}
