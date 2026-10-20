// <copyright file="NewHoldsEnhancementStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Enhancement.Steps;

/// <summary>
/// Step 7 (3D model only): holds the capture photos see that the wall does not have
/// (<see cref="IHoldProposalService.FindInBackgroundAsync"/>, which waits its turn behind another wall's search). Writes
/// only proposal rows; every pending proposal on a panel becomes a review item that adds the hold or turns it down,
/// through the same service as the hold search section.
/// </summary>
public sealed class NewHoldsEnhancementStep : IWallEnhancementStep
{
    /// <inheritdoc/>
    public string Key => "new-holds";

    /// <inheritdoc/>
    public int Order => 700;

    /// <inheritdoc/>
    public string Title => "Finding holds the capture photos show but the wall lacks";

    /// <inheritdoc/>
    public WallEnhancementItemKind Kind => WallEnhancementItemKind.NewHold;

    /// <inheritdoc/>
    public bool NeedsModel => true;

    /// <inheritdoc/>
    public async Task<WallEnhancementStepOutcome> RunAsync(WallEnhancementStepContext context, CancellationToken ct)
    {
        await context.Actors.Proposals.EnsureCanFindAsync(context.WallId, ct);
        await context.Actors.Proposals.FindInBackgroundAsync(context.WallId, null, ct);
        var pending = await context.Actors.Proposals.ListAsync(context.WallId, ct: ct);
        var suggestions = pending
            .Where(p => p.PanelId is not null && !p.IsStale)
            .Select(p => new WallEnhancementSuggestion(null, null, p.Id, null))
            .ToList();
        return new WallEnhancementStepOutcome(
            suggestions.Count == 0
                ? "No missing holds found."
                : $"{WallEnhancementKinds.Count(suggestions.Count, "possible new hold", "possible new holds")} to look at.",
            suggestions);
    }
}
