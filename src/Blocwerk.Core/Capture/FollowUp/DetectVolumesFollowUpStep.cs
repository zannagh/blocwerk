// <copyright file="DetectVolumesFollowUpStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Step 3 (only with a photo-real view): volumes without markers are found in the scene and the holds on them
/// are placed onto their faces (<see cref="IWallVolumeService.DetectFromPipelineAsync"/>). Runs before the
/// protrusion step so both read the same splat in one go; runs again after a retrain. Derived data only: panels,
/// panel hold positions and boulders are never touched.
/// </summary>
public sealed class DetectVolumesFollowUpStep(IWallVolumeService volumes) : ICaptureFollowUpStep
{
    /// <inheritdoc />
    public string Key => "detect-volumes";

    /// <inheritdoc />
    public int Order => 250;

    /// <inheritdoc />
    public string Title => "Photo-real view: finding volumes";

    /// <inheritdoc />
    public bool NeedsPhotoReal => true;

    /// <inheritdoc />
    public async Task<CaptureFollowUpStepResult> RunAsync(CaptureFollowUpContext context, CancellationToken ct)
    {
        if (context.SplatId is null)
        {
            return CaptureFollowUpStepResult.Skipped("no photo-real view");
        }

        var result = await volumes.DetectFromPipelineAsync(context.WallId, ct);
        if (result is null)
        {
            return CaptureFollowUpStepResult.Skipped("the photo-real view could not be searched for volumes");
        }

        return CaptureFollowUpStepResult.Done(Describe(result.Volumes, result.HoldsPlaced));
    }

    /// <summary>"6 volumes found, 82 holds placed on them" (empty when none were found).</summary>
    internal static string Describe(int volumes, int holdsPlaced)
    {
        if (volumes <= 0)
        {
            return string.Empty;
        }

        var found = $"{CaptureFollowUpText.Count(volumes, "volume", "volumes")} found";
        if (holdsPlaced <= 0)
        {
            return found;
        }

        var target = volumes == 1 ? "it" : "them";
        return $"{found}, {CaptureFollowUpText.Count(holdsPlaced, "hold", "holds")} placed on {target}";
    }
}
