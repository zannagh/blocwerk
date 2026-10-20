// <copyright file="DuplicatesEnhancementStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Enhancement.Steps;

/// <summary>
/// Step 5: holds that are probably the same hold recorded twice (<see cref="IHoldDuplicateService.ListAsync"/>), after
/// the shapes are final. Writes nothing: each pair becomes a review item that merges the pair the suggested way
/// (boulders move over) or marks it "not a duplicate", through the same service as the Duplicate holds section.
/// </summary>
public sealed class DuplicatesEnhancementStep : IWallEnhancementStep
{
    /// <summary>The most pairs one run puts up for review (the service's own page limit).</summary>
    public const int MaxPairs = 100;

    /// <inheritdoc/>
    public string Key => "duplicates";

    /// <inheritdoc/>
    public int Order => 500;

    /// <inheritdoc/>
    public string Title => "Looking for holds recorded twice";

    /// <inheritdoc/>
    public WallEnhancementItemKind Kind => WallEnhancementItemKind.Duplicate;

    /// <inheritdoc/>
    public async Task<WallEnhancementStepOutcome> RunAsync(WallEnhancementStepContext context, CancellationToken ct)
    {
        var page = await context.Actors.Duplicates.ListAsync(context.WallId, 0, MaxPairs, ct);
        var suggestions = page.Items
            .Select(i => new WallEnhancementSuggestion(i.Left.Id, i.Right.Id, null, i.SuggestedMode.ToString()))
            .ToList();
        var summary = page.Total == 0
            ? "No duplicate holds found."
            : $"{WallEnhancementKinds.Count(page.Total, "possible duplicate", "possible duplicates")} to look at"
              + (page.Total > suggestions.Count ? $" (the first {suggestions.Count} are in this review)." : ".");
        return new WallEnhancementStepOutcome(summary, suggestions);
    }
}
