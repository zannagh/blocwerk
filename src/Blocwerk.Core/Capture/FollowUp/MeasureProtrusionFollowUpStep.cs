// <copyright file="MeasureProtrusionFollowUpStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Step 4 (at the end of the capture, after the volumes): how far each hold stands out of its facet, measured from the scene
/// (<see cref="IHoldProtrusionService.MeasureFromPipelineAsync"/>): the photo-real view when there is one, else the capture's
/// sparse points (coarser, marked so). Runs again when a photo-real view arrives or is retrained.
/// </summary>
public sealed class MeasureProtrusionFollowUpStep(IHoldProtrusionService protrusion) : ICaptureFollowUpStep
{
    /// <inheritdoc />
    public string Key => "measure-protrusion";

    /// <inheritdoc />
    public int Order => 300;

    /// <inheritdoc />
    public string Title => "Measuring how far the holds stand out";

    /// <inheritdoc />
    public bool NeedsPhotoReal => true;

    /// <inheritdoc />
    public async Task<CaptureFollowUpStepResult> RunAsync(CaptureFollowUpContext context, CancellationToken ct)
    {
        var result = await protrusion.MeasureFromPipelineAsync(context.WallId, ct);
        if (result is null)
        {
            return CaptureFollowUpStepResult.Skipped(
                context.SplatId is null ? "no photo-real view and no sparse points" : "the photo-real view could not be measured");
        }

        var where = result.FromSparsePoints ? "from the sparse points (coarser)" : "in the photo-real view";
        return result.Measured > 0
            ? CaptureFollowUpStepResult.Done($"{CaptureFollowUpText.Count(result.Measured, "hold", "holds")} measured {where}")
            : CaptureFollowUpStepResult.Done(string.Empty);
    }
}
