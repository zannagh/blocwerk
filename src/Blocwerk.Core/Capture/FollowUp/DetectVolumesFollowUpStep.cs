// <copyright file="DetectVolumesFollowUpStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Step 4, a hook: volume detection from the new 3D data will run here. A no-op for now. Whatever it finds must
/// be offered for review, never written onto panels (panels are the source of truth).
/// </summary>
public sealed class DetectVolumesFollowUpStep : ICaptureFollowUpStep
{
    /// <inheritdoc />
    public string Key => "detect-volumes";

    /// <inheritdoc />
    public int Order => 400;

    /// <inheritdoc />
    public string Title => "Looking for volumes";

    /// <inheritdoc />
    public bool NeedsPhotoReal => true;

    /// <inheritdoc />
    public Task<CaptureFollowUpStepResult> RunAsync(CaptureFollowUpContext context, CancellationToken ct) =>
        Task.FromResult(CaptureFollowUpStepResult.Skipped("volume detection is not available yet"));
}
