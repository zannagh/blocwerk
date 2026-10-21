// <copyright file="ColoursEnhancementStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Enhancement.Steps;

/// <summary>
/// Step 4: every hold without a colour gets one suggested from its panel photo when the photo says clearly
/// (<see cref="IHoldColorProposalService.SuggestMissingAsync"/>). A suggestion is the app's existing "Suggested" colour:
/// drawn, but unconfirmed until a person keeps it — which the review does per hold. Holds that have a colour are never
/// touched.
/// </summary>
public sealed class ColoursEnhancementStep : IWallEnhancementStep
{
    /// <inheritdoc/>
    public string Key => "colours";

    /// <inheritdoc/>
    public int Order => 400;

    /// <inheritdoc/>
    public string Title => "Suggesting colours for holds without one";

    /// <inheritdoc/>
    public WallEnhancementItemKind Kind => WallEnhancementItemKind.Colour;

    /// <inheritdoc/>
    public IReadOnlyList<string> OwnBatchLabels => [HoldColorProposalService.BatchLabel];

    /// <inheritdoc/>
    public async Task<WallEnhancementStepOutcome> RunAsync(WallEnhancementStepContext context, CancellationToken ct)
    {
        var report = await context.Actors.Colours.SuggestMissingAsync(context.WallId, ct);
        if (report.Considered == 0)
        {
            return WallEnhancementStepOutcome.Done("Every hold already has a colour.");
        }

        var suggested = report.Proposed + report.FromLinked;
        return WallEnhancementStepOutcome.Done(
            $"{WallEnhancementKinds.Count(suggested, "colour", "colours")} suggested; "
            + $"{WallEnhancementKinds.Count(report.LeftUnset, "hold stays", "holds stay")} without one because the photo is not clear.");
    }
}
