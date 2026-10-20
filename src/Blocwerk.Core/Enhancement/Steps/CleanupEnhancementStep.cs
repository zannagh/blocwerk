// <copyright file="CleanupEnhancementStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Enhancement.Steps;

/// <summary>
/// Step 3: automatic outlines (also the ones step 1 just traced) are smoothed and kept from overlapping
/// (<see cref="IHoldShapeCleanupService.ApplyAsync"/>). Shapes a person drew are never changed.
/// </summary>
public sealed class CleanupEnhancementStep : IWallEnhancementStep
{
    /// <inheritdoc/>
    public string Key => "cleanup";

    /// <inheritdoc/>
    public int Order => 300;

    /// <inheritdoc/>
    public string Title => "Smoothing hold shapes and keeping them apart";

    /// <inheritdoc/>
    public WallEnhancementItemKind Kind => WallEnhancementItemKind.Shape;

    /// <inheritdoc/>
    public IReadOnlyList<string> OwnBatchLabels => [HoldShapeCleanupService.BatchLabel];

    /// <inheritdoc/>
    public async Task<WallEnhancementStepOutcome> RunAsync(WallEnhancementStepContext context, CancellationToken ct)
    {
        var result = await context.Actors.Cleanup.ApplyAsync(context.WallId, ct);
        var text = result.Changed == 0
            ? "Every automatic hold shape was already smooth and separate."
            : $"{WallEnhancementKinds.Count(result.Changed, "hold shape", "hold shapes")} smoothed or separated.";
        return WallEnhancementStepOutcome.Done(result.StillOverlapping > 0
            ? $"{text} {result.StillOverlapping} still overlap something and were kept as they are."
            : text);
    }
}
