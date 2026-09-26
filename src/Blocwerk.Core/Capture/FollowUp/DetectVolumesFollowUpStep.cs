// <copyright file="DetectVolumesFollowUpStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Step 3 (at the end of the capture): volumes without markers are found in the scene and the holds on them
/// are placed onto their faces (<see cref="IWallVolumeService.DetectFromPipelineAsync"/>): in the photo-real view when there
/// is one, else in the capture's sparse points (coarser). Runs before the protrusion step; runs again when a photo-real
/// view arrives or is retrained. Derived data only: panels,
/// panel hold positions and boulders are never touched.
/// </summary>
public sealed class DetectVolumesFollowUpStep(IWallVolumeService volumes) : ICaptureFollowUpStep
{
    /// <inheritdoc />
    public string Key => "detect-volumes";

    /// <inheritdoc />
    public int Order => 250;

    /// <inheritdoc />
    public string Title => "Finding volumes";

    /// <inheritdoc />
    public bool NeedsPhotoReal => true;

    /// <inheritdoc />
    public async Task<CaptureFollowUpStepResult> RunAsync(CaptureFollowUpContext context, CancellationToken ct)
    {
        var result = await volumes.DetectFromPipelineAsync(context.WallId, ct);
        if (result is null)
        {
            return CaptureFollowUpStepResult.Skipped(
                context.SplatId is null ? "no photo-real view and no sparse points" : "the photo-real view could not be searched for volumes");
        }

        var text = Describe(result.Volumes, result.HoldsPlaced);
        return CaptureFollowUpStepResult.Done(result.FromSparsePoints && text.Length > 0 ? $"{text} (from the sparse points, coarser)" : text);
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
