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
    /// <summary>Pairs per page asked of the service (its own page limit).</summary>
    public const int PageSize = 100;

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
        // Every pair, page by page (the service hands out at most a page at a time).
        var suggestions = new List<WallEnhancementSuggestion>();
        var total = 0;
        do
        {
            var page = await context.Actors.Duplicates.ListAsync(context.WallId, suggestions.Count, PageSize, ct);
            total = page.Total;
            suggestions.AddRange(page.Items.Select(i => new WallEnhancementSuggestion(i.Left.Id, i.Right.Id, null, i.SuggestedMode.ToString())));
            if (page.Items.Count == 0)
            {
                break;
            }
        }
        while (suggestions.Count < total);

        var summary = total == 0
            ? "No duplicate holds found."
            : $"{WallEnhancementKinds.Count(total, "possible duplicate", "possible duplicates")} to look at.";
        return new WallEnhancementStepOutcome(summary, suggestions);
    }
}
