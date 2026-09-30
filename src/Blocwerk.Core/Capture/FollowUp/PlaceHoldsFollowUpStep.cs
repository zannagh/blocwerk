// <copyright file="PlaceHoldsFollowUpStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Step 1: the wall's live holds that are not on the new model yet (or were placed on an earlier model by this
/// same texture matching: a re-solve keeps the facet ids but moves their planes) are placed on its facet textures from their
/// panel photos (<see cref="IHoldTexturePlacementService.PlaceFromPipelineAsync"/>). Holds placed by markers or
/// by an edit are never touched; a hold's panel position and shape never change. A photo the new textures cannot
/// be registered to is seeded from its holds' previous placements and from holds linked to other photos; what
/// still fails keeps its previous placement, carried onto the new model. The run is revertable.
/// </summary>
public sealed class PlaceHoldsFollowUpStep(IHoldTexturePlacementService placement) : ICaptureFollowUpStep
{
    /// <summary>The step's key (it is re-run after a texture re-render).</summary>
    public const string StepKey = "place-holds";

    /// <inheritdoc />
    public string Key => StepKey;

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

        if (result.Placed == 0)
        {
            return CaptureFollowUpStepResult.Done("no existing hold could be matched to the 3D model's photos");
        }

        // Carried: a photo the new textures could not be registered to kept its holds' previous placements.
        var text = Describe(result.Placed, result.Panels.Sum(p => p.Carried));
        var unmeasured = HoldPlacementUnmeasured.Text(result.Panels);
        return CaptureFollowUpStepResult.Done(unmeasured.Length > 0 ? $"{text}. {unmeasured}" : text);
    }

    /// <summary>"856 holds placed on the 3D model (12 kept from the previous model)".</summary>
    /// <param name="placed">Holds placed on the model from their photos.</param>
    /// <param name="carried">Of those, the ones whose previous placement was kept.</param>
    /// <returns>The phrase.</returns>
    internal static string Describe(int placed, int carried)
    {
        var text = $"{CaptureFollowUpText.Count(placed, "hold", "holds")} placed on the 3D model";
        return carried > 0 ? $"{text} ({carried} kept from the previous model)" : text;
    }

    /// <summary>
    /// The live form for the capture history: <see cref="Describe"/> of the holds placed from photos now, plus how many
    /// are left unmeasured on purpose (the reasons are known only when the step runs).
    /// </summary>
    /// <param name="placed">Live holds placed on the model from their photos.</param>
    /// <param name="carried">Of those, the ones carried from the previous model.</param>
    /// <param name="unmeasured">Live holds a run left unmeasured on purpose.</param>
    /// <returns>The phrase.</returns>
    internal static string DescribeLive(int placed, int carried, int unmeasured)
    {
        var text = placed == 0 ? "no hold placed on the 3D model from the photos" : Describe(placed, carried);
        return unmeasured > 0 ? $"{text}. {CaptureFollowUpText.Count(unmeasured, "hold", "holds")} left unmeasured" : text;
    }
}
