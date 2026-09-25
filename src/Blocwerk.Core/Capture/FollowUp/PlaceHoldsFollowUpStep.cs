// <copyright file="PlaceHoldsFollowUpStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Step 1: the wall's live holds that are not on the new model yet (or were placed on an earlier model by this
/// same texture matching: a re-solve keeps the facet ids but moves their planes) are placed on its facet textures from their
/// panel photos (<see cref="IHoldTexturePlacementService.PlaceFromPipelineAsync"/>). Holds placed by markers or
/// by an edit are never touched; a hold's panel position and shape never change. The run is revertable.
/// </summary>
public sealed class PlaceHoldsFollowUpStep(IHoldTexturePlacementService placement) : ICaptureFollowUpStep
{
    /// <inheritdoc />
    public string Key => "place-holds";

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public string Title => "Placing the existing holds on the 3D model";

    /// <inheritdoc />
    public bool NeedsPhotoReal => false;

    /// <inheritdoc />
    public async Task<CaptureFollowUpStepResult> RunAsync(CaptureFollowUpContext context, CancellationToken ct)
    {
        var result = await placement.PlaceFromPipelineAsync(context.WallId, context.ModelId, context.ActingUserId, ct);
        if (result is null)
        {
            return CaptureFollowUpStepResult.Skipped("no unplaced holds (or placing is not available here)");
        }

        return result.Placed > 0
            ? CaptureFollowUpStepResult.Done($"{CaptureFollowUpText.Count(result.Placed, "hold", "holds")} placed on the 3D model")
            : CaptureFollowUpStepResult.Done("no existing hold could be matched to the 3D model's photos");
    }
}
