// <copyright file="OutlinesEnhancementStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Enhancement.Steps;

/// <summary>
/// Step 1: holds that are still plain circles get their real outline traced from the current panel photos
/// (<see cref="IHoldOutlineUpgradeService.ApplyAsync"/>, hand-placed holds left alone). Positions and boulders are not
/// changed. The service queues the 3D footprints of exactly the outlined holds itself.
/// </summary>
public sealed class OutlinesEnhancementStep : IWallEnhancementStep
{
    /// <inheritdoc/>
    public string Key => "outlines";

    /// <inheritdoc/>
    public int Order => 100;

    /// <inheritdoc/>
    public string Title => "Tracing the real outlines of circle holds";

    /// <inheritdoc/>
    public WallEnhancementItemKind Kind => WallEnhancementItemKind.Shape;

    /// <inheritdoc/>
    public async Task<WallEnhancementStepOutcome> RunAsync(WallEnhancementStepContext context, CancellationToken ct)
    {
        var status = await context.Actors.Outlines.GetStatusAsync(context.WallId, ct);
        if (!status.Enabled)
        {
            return WallEnhancementStepOutcome.Skip("Outline detection is switched off on this server.");
        }

        var result = await context.Actors.Outlines.ApplyAsync(context.WallId, new HoldOutlineUpgradeOptions(false), ct);
        return WallEnhancementStepOutcome.Done(result.Eligible == 0
            ? "Every hold already had an outline."
            : $"{WallEnhancementKinds.Count(result.Outlined, "hold", "holds")} of {result.Eligible} circle holds got their real outline.");
    }
}
