// <copyright file="RefineFootprintsFollowUpStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Step 2: the 3D hold shapes (contact footprints on their facets) are refined from the new capture photos that
/// see them (<see cref="IHoldFootprintService.RefineFromPipelineAsync"/>). Writes only the derived footprint,
/// never a hold's panel outline.
/// </summary>
public sealed class RefineFootprintsFollowUpStep(IHoldFootprintService footprints) : ICaptureFollowUpStep
{
    /// <inheritdoc />
    public string Key => "refine-footprints";

    /// <inheritdoc />
    public int Order => 200;

    /// <inheritdoc />
    public string Title => "Refining the 3D hold shapes";

    /// <inheritdoc />
    public bool NeedsPhotoReal => false;

    /// <inheritdoc />
    public async Task<CaptureFollowUpStepResult> RunAsync(CaptureFollowUpContext context, CancellationToken ct)
    {
        var result = await footprints.RefineFromPipelineAsync(context.WallId, ct);
        if (result is null)
        {
            return CaptureFollowUpStepResult.Skipped("footprint refinement could not run (no outlines or no model)");
        }

        return CaptureFollowUpStepResult.Done(Describe(result.MultiView, result.SingleView));
    }

    /// <summary>"653 hold shapes refined from several photos and 12 from one photo" (empty when none).</summary>
    internal static string Describe(int multiView, int singleView)
    {
        if (multiView > 0)
        {
            var main = $"{CaptureFollowUpText.Count(multiView, "hold shape", "hold shapes")} refined from several photos";
            return singleView > 0 ? $"{main} and {singleView} from one photo" : main;
        }

        return singleView > 0 ? $"{CaptureFollowUpText.Count(singleView, "hold shape", "hold shapes")} refined from one photo" : string.Empty;
    }
}
