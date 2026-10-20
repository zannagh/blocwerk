// <copyright file="StacksEnhancementStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.HoldStacks;

namespace Blocwerk.Core.Enhancement.Steps;

/// <summary>
/// Step 2: a hold that lies on another one (a small hold on a macro or volume) is stacked on it
/// (<see cref="IHoldStackService.ApplyAsync"/>). Before the clean-up on purpose: the clean-up keeps holds apart except
/// stacked ones, so stacking first stops it from trimming a hold off the macro it sits on.
/// </summary>
public sealed class StacksEnhancementStep : IWallEnhancementStep
{
    /// <inheritdoc/>
    public string Key => "stacks";

    /// <inheritdoc/>
    public int Order => 200;

    /// <inheritdoc/>
    public string Title => "Finding holds that sit on other holds";

    /// <inheritdoc/>
    public WallEnhancementItemKind Kind => WallEnhancementItemKind.Stack;

    /// <inheritdoc/>
    public IReadOnlyList<string> OwnBatchLabels => [HoldStackService.BatchLabel];

    /// <inheritdoc/>
    public async Task<WallEnhancementStepOutcome> RunAsync(WallEnhancementStepContext context, CancellationToken ct)
    {
        var report = await context.Actors.Stacks.ApplyAsync(context.WallId, null, ct);
        return WallEnhancementStepOutcome.Done(report.Stacks.Count == 0
            ? "No hold sits on another one."
            : $"{WallEnhancementKinds.Count(report.Stacks.Count, "hold sits", "holds sit")} on another hold.");
    }
}
