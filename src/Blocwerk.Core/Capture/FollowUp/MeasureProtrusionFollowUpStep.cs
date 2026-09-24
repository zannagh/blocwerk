// <copyright file="MeasureProtrusionFollowUpStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Step 4 (only with a photo-real view, after the volumes): how far each hold stands out of its facet, measured from the scene
/// (<see cref="IHoldProtrusionService.MeasureFromPipelineAsync"/>). Runs again after a retrain.
/// </summary>
public sealed class MeasureProtrusionFollowUpStep(IHoldProtrusionService protrusion) : ICaptureFollowUpStep
{
    /// <inheritdoc />
    public string Key => "measure-protrusion";

    /// <inheritdoc />
    public int Order => 300;

    /// <inheritdoc />
    public string Title => "Photo-real view: measuring the holds";

    /// <inheritdoc />
    public bool NeedsPhotoReal => true;

    /// <inheritdoc />
    public async Task<CaptureFollowUpStepResult> RunAsync(CaptureFollowUpContext context, CancellationToken ct)
    {
        if (context.SplatId is null)
        {
            return CaptureFollowUpStepResult.Skipped("no photo-real view");
        }

        var result = await protrusion.MeasureFromPipelineAsync(context.WallId, ct);
        if (result is null)
        {
            return CaptureFollowUpStepResult.Skipped("the photo-real view could not be measured");
        }

        return result.Measured > 0
            ? CaptureFollowUpStepResult.Done($"{CaptureFollowUpText.Count(result.Measured, "hold", "holds")} measured in the photo-real view")
            : CaptureFollowUpStepResult.Done(string.Empty);
    }
}
