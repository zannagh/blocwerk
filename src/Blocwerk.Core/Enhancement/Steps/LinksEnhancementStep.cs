// <copyright file="LinksEnhancementStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Enhancement.Steps;

/// <summary>
/// Step 6 (3D model only): holds on two neighbouring panels that the 3D model places on the same spot
/// (<see cref="IHoldLinkSuggestionService.ListAsync"/>, which refreshes the suggestions first). Writes nothing: each pair
/// is linked or turned down in the review, through the same service as the 3D view's link suggestions.
/// </summary>
public sealed class LinksEnhancementStep : IWallEnhancementStep
{
    /// <inheritdoc/>
    public string Key => "links";

    /// <inheritdoc/>
    public int Order => 600;

    /// <inheritdoc/>
    public string Title => "Finding the same hold on two panels";

    /// <inheritdoc/>
    public WallEnhancementItemKind Kind => WallEnhancementItemKind.Link;

    /// <inheritdoc/>
    public bool NeedsModel => true;

    /// <inheritdoc/>
    public async Task<WallEnhancementStepOutcome> RunAsync(WallEnhancementStepContext context, CancellationToken ct)
    {
        var list = await context.Actors.Links.ListAsync(context.WallId, ct);
        var suggestions = list.Select(s => new WallEnhancementSuggestion(s.A.HoldId, s.B.HoldId, null, null)).ToList();
        return new WallEnhancementStepOutcome(
            suggestions.Count == 0
                ? "No unlinked pairs found."
                : $"{WallEnhancementKinds.Count(suggestions.Count, "pair", "pairs")} of holds that look like the same hold.",
            suggestions);
    }
}
